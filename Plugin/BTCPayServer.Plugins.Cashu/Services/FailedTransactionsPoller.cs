using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Cashu.Services;

public class FailedTransactionsPoller(
    CashuDbContextFactory dbContextFactory,
    CashuPaymentService cashuPaymentService,
    StoreRepository storeRepository,
    InvoiceRepository invoiceRepository,
    ILogger<FailedTransactionsPoller> logger
) : IHostedService, IDisposable
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Max transactions to poll in a single cycle.
    /// </summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Max concurrent polls per mint to avoid overwhelming a single mint.
    /// </summary>
    public int MaxConcurrencyPerMint { get; init; } = 3;

    private readonly ConcurrentDictionary<Guid, byte> _tracked = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mintSemaphores = new();
    private readonly SemaphoreSlim _pollGuard = new(1, 1);
    private CancellationTokenSource? _cts;
    private Timer? _timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(
            _ => _ = PollAllAsync(_cts.Token),
            null,
            TimeSpan.FromSeconds(30), // initial delay
            PollInterval
        );
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves a failed transaction to the DB and starts tracking it for automatic polling.
    /// </summary>
    public async Task AddFailedTx(FailedTransaction ft, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateContext();
        await db.FailedTransactions.AddAsync(ft, ct);
        await db.SaveChangesAsync(ct);

        _tracked.TryAdd(ft.Id, 0);
        logger.LogDebug("(Cashu) Added failed tx {Id} for tracking (Invoice: {InvoiceId})", ft.Id, ft.InvoiceId);
    }

    /// <summary>
    /// Polls a single failed transaction and handles the result.
    /// Can be called from the controller for manual polling.
    /// Returns the poll result so callers can act on it (e.g. show UI feedback).
    /// </summary>
    public async Task<CashuPaymentService.PollResult> PollTransaction(
        FailedTransaction ftx,
        CancellationToken ct = default)
    {
        var storeData = await storeRepository.FindStore(ftx.StoreId);
        if (storeData == null)
            return new CashuPaymentService.PollResult
            {
                State = CashuPaymentState.Failed,
                Error = new InvalidOperationException($"Store {ftx.StoreId} not found")
            };

        var result = ftx.OperationType == OperationType.Melt
            ? await cashuPaymentService.PollFailedMelt(ftx, storeData, ct)
            : await cashuPaymentService.PollFailedSwap(ftx, storeData, ct);

        await using var db = dbContextFactory.CreateContext();
        db.FailedTransactions.Attach(ftx);

        ftx.RetryCount++;
        ftx.LastRetried = DateTimeOffset.UtcNow;

        switch (result.State)
        {
            case CashuPaymentState.Success:
                if (result.ResultProofs != null)
                {
                    await cashuPaymentService.AddProofsToDb(
                        result.ResultProofs,
                        ftx.StoreId,
                        ftx.MintUrl,
                        ProofState.Available
                    );
                }
                ftx.Resolved = true;
                ftx.Details = "Resolved by poller";
                _tracked.TryRemove(ftx.Id, out _);
                logger.LogInformation(
                    "(Cashu) Resolved failed tx {Id} (Invoice: {InvoiceId})",
                    ftx.Id, ftx.InvoiceId);
                break;

            case CashuPaymentState.Failed:
                ftx.Details = result.Error?.Message ?? "Permanently failed";
                _tracked.TryRemove(ftx.Id, out _);
                logger.LogWarning(
                    "(Cashu) Failed tx {Id} is permanently failed: {Details}",
                    ftx.Id, ftx.Details);
                break;

            case CashuPaymentState.Pending:
                ftx.Details = result.Error?.Message ?? "Still pending";
                break;
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        // Prevent overlapping cycles
        if (!_pollGuard.Wait(0))
            return;

        try
        {
            await using var db = dbContextFactory.CreateContext();
            var unresolvedTxs = await db.FailedTransactions
                .Where(ft => !ft.Resolved)
                .OrderBy(ft => ft.LastRetried)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (unresolvedTxs.Count == 0)
                return;

            // Make sure in-memory tracking is in sync
            foreach (var ftx in unresolvedTxs)
                _tracked.TryAdd(ftx.Id, 0);

            logger.LogDebug("(Cashu) Polling {Count} unresolved failed transactions", unresolvedTxs.Count);

            // Group by mint and poll concurrently, respecting per-mint limits
            var byMint = unresolvedTxs.GroupBy(ft => ft.MintUrl);
            var mintTasks = byMint.Select(group => PollMintGroupAsync(group.ToList(), ct));
            await Task.WhenAll(mintTasks);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "(Cashu) Error during failed transaction poll cycle");
        }
        finally
        {
            _pollGuard.Release();
        }
    }

    private async Task PollMintGroupAsync(List<FailedTransaction> txs, CancellationToken ct)
    {
        var semaphore = _mintSemaphores.GetOrAdd(txs[0].MintUrl, _ => new SemaphoreSlim(MaxConcurrencyPerMint));

        var tasks = txs.Select(async ftx =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var invoice = await invoiceRepository.GetInvoice(ftx.InvoiceId);
                if (invoice == null || invoice.ExpirationTime <= DateTimeOffset.UtcNow)
                {
                    _tracked.TryRemove(ftx.Id, out _);
                    return;
                }

                await PollTransaction(ftx, ct);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "(Cashu) Error polling failed tx {Id}", ftx.Id);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Dispose();
        _pollGuard.Dispose();
        foreach (var sem in _mintSemaphores.Values)
            sem.Dispose();
    }
}
