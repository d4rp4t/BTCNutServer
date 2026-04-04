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

    /// <summary>
    /// Max retries before giving up on a failed transaction.
    /// </summary>
    public int MaxRetries { get; init; } = 20;

    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromHours(2);

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
                logger.LogInformation(
                    "(Cashu) Resolved failed tx {Id} (Invoice: {InvoiceId})",
                    ftx.Id, ftx.InvoiceId);
                break;

            case CashuPaymentState.Failed:
                ftx.Details = result.Error?.Message ?? "Permanently failed";
                logger.LogWarning(
                    "(Cashu) Failed tx {Id} is permanently failed: {Details}",
                    ftx.Id, ftx.Details);
                break;

            case CashuPaymentState.Pending:
                if (ftx.RetryCount >= MaxRetries)
                {
                    ftx.Resolved = true;
                    ftx.Details = $"Gave up after {MaxRetries} retries";
                    logger.LogWarning(
                        "(Cashu) Giving up on failed tx {Id} (Invoice: {InvoiceId}) after {MaxRetries} retries",
                        ftx.Id, ftx.InvoiceId, MaxRetries);
                }
                else
                {
                    ftx.Details = result.Error?.Message ?? "Still pending";
                }
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

            var now = DateTimeOffset.UtcNow;
            var eligibleTxs = unresolvedTxs
                .Where(ft => now >= ft.LastRetried + GetBackoffDelay(ft.RetryCount))
                .ToList();

            if (eligibleTxs.Count == 0)
                return;

            logger.LogDebug("(Cashu) Polling {Count} unresolved failed transactions", eligibleTxs.Count);

            var byMint = eligibleTxs.GroupBy(ft => ft.MintUrl).ToList();
            var mintTasks = byMint.Select(group => PollMintGroupAsync(group.ToList(), ct));
            await Task.WhenAll(mintTasks);

            foreach (var group in byMint)
            {
                if (_mintSemaphores.TryRemove(group.Key, out var sem))
                    sem.Dispose();
            }
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
                    await using var db = dbContextFactory.CreateContext();
                    db.FailedTransactions.Attach(ftx);
                    ftx.Resolved = true;
                    ftx.Details = invoice == null ? "Invoice not found" : "Invoice expired";
                    await db.SaveChangesAsync(ct);
                    return;
                }

                var result = await PollTransaction(ftx, ct);
                if (result.Success)
                    await cashuPaymentService.RegisterPaymentForFailedTx(ftx, ct);
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

    private TimeSpan GetBackoffDelay(int retryCount)
    {
        var ticks = (long)(PollInterval.Ticks * Math.Pow(2, retryCount));
        return ticks > MaxBackoffDelay.Ticks ? MaxBackoffDelay : TimeSpan.FromTicks(ticks);
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
