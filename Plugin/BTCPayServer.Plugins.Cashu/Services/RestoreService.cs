#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using DotNut.Abstractions;
using DotNut.Api;
using DotNut.NBitcoin.BIP39;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Cashu.Services;

public class RestoreService : IHostedService
{
    private readonly ILogger<RestoreService> _logger;
    private readonly SemaphoreSlim _mintSemaphore = new(5);
    private readonly ConcurrentDictionary<string, RestoreStatus> _restoreStatuses = new();
    private readonly ConcurrentQueue<RestoreJob> _restoreQueue = new();
    private readonly CashuDbContextFactory _dbContextFactory;
    private readonly MintManager _mintManager;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public RestoreService(ILogger<RestoreService> logger, CashuDbContextFactory dbContextFactory, MintManager mintManager)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _mintManager = mintManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("(Cashu) Restore service starting");

        _cancellationTokenSource = new CancellationTokenSource();
        _processingTask = ProcessRestoreQueueAsync(_cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("(Cashu) Restore service stopping");

        _cancellationTokenSource?.Cancel();

        if (_processingTask != null)
        {
            await Task.WhenAny(_processingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }

        _mintSemaphore.Dispose();
    }

    /// <summary>
    /// Add new restore task to queue
    /// </summary>
    public string QueueRestore(string storeId, List<string> mintUrls, string seed)
    {
        var jobId = Guid.NewGuid().ToString();
        var job = new RestoreJob
        {
            JobId = jobId,
            StoreId = storeId,
            MintUrls = mintUrls,
            Seed = seed,
            QueuedAt = DateTime.UtcNow,
        };

        _restoreQueue.Enqueue(job);

        _restoreStatuses[jobId] = new RestoreStatus
        {
            JobId = jobId,
            StoreId = storeId,
            Status = RestoreState.Queued,
            TotalMints = mintUrls.Count,
            ProcessedMints = 0,
            StartedAt = null,
            CompletedAt = null,
            Errors = new List<string>(),
            RestoredMints = new List<RestoredMint>(),
        };

        _logger.LogDebug("(Cashu) Restore job {JobId} queued for store {StoreId} with {MintCount} mints", jobId, storeId, mintUrls.Count);

        return jobId;
    }

    /// <summary>
    /// Get restore status
    /// </summary>
    public RestoreStatus? GetRestoreStatus(string jobId)
    {
        return _restoreStatuses.TryGetValue(jobId, out var status) ? status : null;
    }

    /// <summary>
    /// Get restore statuses per store (for every mint)
    /// </summary>
    public List<RestoreStatus> GetStoreRestoreStatuses(string storeId)
    {
        return _restoreStatuses
            .Values.Where(s => s.StoreId == storeId)
            .OrderByDescending(s => s.QueuedAt)
            .ToList();
    }

    private async Task ProcessRestoreQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_restoreQueue.TryDequeue(out var job))
                {
                    await ProcessRestoreJobAsync(job, cancellationToken);
                }
                else
                {
                    // wait for new jobs
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "(Cashu) Error processing restore queue");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private async Task ProcessRestoreJobAsync(RestoreJob job, CancellationToken cancellationToken)
    {
        var status = _restoreStatuses[job.JobId];
        status.Status = RestoreState.Processing;
        status.StartedAt = DateTime.UtcNow;

        _logger.LogDebug(
            "(Cashu) Starting restore job {JobId} for store {StoreId} ({MintCount} mints)",
            job.JobId,
            job.StoreId,
            job.MintUrls.Count
        );

        try
        {
            var restoreTasks = job.MintUrls.Select(async mintUrl =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                await _mintSemaphore.WaitAsync(cancellationToken);

                lock (status)
                {
                    status.CurrentMints.Add(mintUrl);
                    status.CurrentMint = mintUrl; // backwards compat
                }

                try
                {
                    _logger.LogDebug(
                        "(Cashu) Restoring from mint {MintUrl} for store {StoreId}",
                        mintUrl,
                        job.StoreId
                    );

                    var restoredMint = await RestoreFromMintAsync(
                        job.StoreId,
                        mintUrl,
                        job.Seed,
                        cancellationToken
                    );

                    if (restoredMint.Proofs.Count != 0)
                    {
                        await SaveRecoveredTokensAsync(
                            job.StoreId,
                            mintUrl,
                            restoredMint.Proofs,
                            cancellationToken
                        );
                    }

                    lock (status)
                    {
                        status.RestoredMints.Add(restoredMint);
                        status.ProcessedMints++;
                    }

                    _logger.LogDebug(
                        "(Cashu) Recovered {ProofCount} proofs from {MintUrl}",
                        restoredMint.Proofs.Count,
                        mintUrl
                    );
                }
                catch (Exception ex)
                {
                    var error = $"Error restoring from {mintUrl}: {ex.Message}";
                    lock (status)
                    {
                        status.UnreachableMints.Add(mintUrl);
                        status.Errors.Add(error);
                    }
                    _logger.LogDebug(ex, "(Cashu) {Error}", error);
                }
                finally
                {
                    lock (status)
                    {
                        status.CurrentMints.Remove(mintUrl);
                    }
                    _mintSemaphore.Release();
                }
            });

            await Task.WhenAll(restoreTasks);

            if (cancellationToken.IsCancellationRequested)
            {
                status.Status = RestoreState.Cancelled;
                return;
            }

            status.Status = status.Errors.Count > 0
                ? RestoreState.CompletedWithErrors
                : RestoreState.Completed;
            status.CompletedAt = DateTime.UtcNow;
            await SaveWalletConfig(job.StoreId, new Mnemonic(job.Seed), cancellationToken);

            _logger.LogDebug(
                "(Cashu) Restore job {JobId} completed{ErrorInfo}",
                job.JobId,
                status.Errors.Count > 0 ? $" with {status.Errors.Count} errors" : ""
            );
        }
        catch (Exception ex)
        {
            status.Status = RestoreState.Failed;
            status.Errors.Add($"Fatal error: {ex.Message}");
            status.CompletedAt = DateTime.UtcNow;

            _logger.LogDebug(ex, "(Cashu) Restore job {JobId} failed", job.JobId);
        }
    }

    private async Task<RestoredMint> RestoreFromMintAsync(
        string storeId,
        string mintUrl,
        string seed,
        CancellationToken ct
    )
    {
        var mint = CashuUtils.GetCashuHttpClient(mintUrl);
        var counter = new DbCounter(_dbContextFactory, storeId);
        var wallet = Wallet.Create().WithMint(mint).WithMnemonic(seed).WithCounter(counter);

        var proofs = await wallet.Restore().ProcessAsync(ct);

        var proofList = proofs.ToList();

        var keysetUnits = await wallet.GetActiveKeysetIdsWithUnits(ct);
        var amountsPerUnit = new Dictionary<string, ulong>();

        if (keysetUnits != null)
        {
            foreach (var keyValuePair in keysetUnits)
            {
                var key = keyValuePair.Key;

                amountsPerUnit[key] =
                    amountsPerUnit.GetValueOrDefault(key)
                    + proofList.Where(p => p.Id == keyValuePair.Value).Select(p => p.Amount).Sum();
            }
        }

        return new RestoredMint()
        {
            MintUrl = mintUrl,
            Proofs = proofList,
            Balances = amountsPerUnit,
        };
    }

    private async Task SaveRecoveredTokensAsync(
        string storeId,
        string mintUrl,
        List<Proof> proofs,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _mintManager.GetOrCreateMint(mintUrl);

            await using var db = _dbContextFactory.CreateContext();

            // add proofs
            db.Proofs.AddRange(StoredProof.FromBatch(proofs, storeId, ProofState.Available));

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "(Cashu) Error saving recovered tokens to db");
        }
    }

    private async Task SaveWalletConfig(string storeId, Mnemonic mnemonic, CancellationToken ct)
    {
        try
        {
            await using var db = _dbContextFactory.CreateContext();
            var config = await db.CashuWalletConfig.FirstOrDefaultAsync(
                c => c.StoreId == storeId,
                ct
            );
            if (config == null)
            {
                config = new CashuWalletConfig
                {
                    StoreId = storeId,
                    WalletMnemonic = mnemonic,
                    Verified = true,
                };
                db.CashuWalletConfig.Add(config);
            }
            else
            {
                config.WalletMnemonic = mnemonic;
                config.Verified = true;
                db.CashuWalletConfig.Update(config);
            }
            await db.SaveChangesAsync(ct);
            // counter is already set and if anything happens, will be overriden while restore process.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "(Cashu) Error saving wallet config to db");
        }
    }
}

public class RestoreJob
{
    public string JobId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public List<string> MintUrls { get; set; } = new();
    public string Seed { get; set; } = string.Empty;
    public DateTime QueuedAt { get; set; }
}

public class RestoreStatus
{
    public string JobId { get; set; } = string.Empty;
    public string StoreId { get; set; }
    public RestoreState Status { get; set; }
    public int TotalMints { get; set; }
    public int ProcessedMints { get; set; }
    public string? CurrentMint { get; set; }
    public HashSet<string> CurrentMints { get; set; } = new();
    public List<string> UnreachableMints { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<RestoredMint> RestoredMints { get; set; }
}

public class RestoredMint
{
    public string MintUrl { get; set; } = string.Empty;
    public List<Proof> Proofs { get; set; }
    public Dictionary<string, ulong> Balances { get; set; }
}
