using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using DotNut.Abstractions;
using DotNut.Abstractions.Websockets;
using DotNut.Api;
using DotNut.ApiModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Cashu.CashuAbstractions;

public class MintListener(CashuDbContextFactory dbContextFactory, ILogger<MintListener> logger)
    : IHostedService, IDisposable
{
    /// <summary>
    /// Interval for periodic cleanup and retry of stuck quotes.
    /// Default: 1 minute. Tests can lower this for faster feedback.
    /// </summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(2);
    private readonly WebsocketService _wsService = new(new WebsocketServiceOptions { AutoReconnect = false });
    private readonly ConcurrentDictionary<string, (Subscription Sub, CancellationTokenSource Cts)> _activeSubscriptions = new();
    private readonly ConcurrentDictionary<string, CashuListenerRegistration> _listeners = new();
    private readonly ConcurrentDictionary<string, byte> _mintingQuotes = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingPayments = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _payLocks = new();
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private Timer? _expiryTimer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = Task.Run(() => RecoverAsync(_cts.Token), _cts.Token);
        _expiryTimer = new Timer(_ => _ =
                CleanupExpiredQuotesAsync(), null,
            CleanupInterval, CleanupInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
            await _cts.CancelAsync();

        if (_backgroundTask != null)
        {
            try { await _backgroundTask; }
            catch (OperationCanceledException) { }
        }

        foreach (var registration in _listeners.Values)
            registration.NotificationChannel.Writer.TryComplete();

        foreach (var (sub, subCts) in _activeSubscriptions.Values)
        {
            subCts.Dispose();
            await sub.DisposeAsync();
        }

        await _wsService.DisposeAsync();
    }

    public async Task SubscribeQuoteAsync(string mintUrl, string quoteId, CancellationToken ct)
    {
        var key = $"{mintUrl}|{quoteId}";
        if (_activeSubscriptions.ContainsKey(key))
            return;

        try
        {
            var subscription = await _wsService.SubscribeToSingleMintQuoteAsync(mintUrl, quoteId, ct);
            var subCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);

            if (_activeSubscriptions.TryAdd(key, (subscription, subCts)))
            {
                _ = Task.Run(() => ReadSubscriptionAsync(key, subscription, subCts.Token));
            }
            else
            {
                await subscription.DisposeAsync();
                subCts.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to subscribe to quote {QuoteId} on {Mint}", quoteId, mintUrl);
        }
    }

    public CashuListenerRegistration RegisterListener(string storeId, string mintUrl)
    {
        var registration = new CashuListenerRegistration
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            MintUrl = mintUrl,
            NotificationChannel = Channel.CreateUnbounded<LightningInvoice>()
        };
        _listeners.TryAdd(registration.Id, registration);
        logger.LogDebug("Registered listener {Id} for store {StoreId} mint {Mint}",
            registration.Id, storeId, mintUrl);
        return registration;
    }

    public void TrackPendingPayment(Guid paymentId)
    {
        _pendingPayments.TryAdd(paymentId, 0);
    }

    public SemaphoreSlim GetPayLock(string storeId) =>
        _payLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));

    public void UnregisterListener(string registrationId)
    {
        if (!_listeners.TryRemove(registrationId, out var registration))
        {
            return;
        }
        registration.NotificationChannel.Writer.TryComplete();
        logger.LogDebug("Unregistered listener {Id}", registrationId);
    }


    /// <summary>
    ///  recover all non-terminal quotes.
    /// - UNPAID: subscribe via WS - mint sends current state as first notification --> check if there's nothing new
    /// - PAID: lightning was paid but tokens weren't minted yet --> mint em
    /// - ISSUED with no proofs: crash between mint API response and DB save --> signature recovery
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateContext();
            var quotes = await db.LightningInvoices
                .Where(p => p.QuoteState == "UNPAID"
                         || p.QuoteState == "PAID"
                         || (p.QuoteState == "ISSUED" && p.Proofs == null))
                .ToListAsync(ct);

            // pending melt payments
            var pendingPayments = await db.LightningPayments
                .Where(p => p.QuoteState == "PENDING")
                .ToListAsync(ct);
            foreach (var p in pendingPayments)
                _pendingPayments.TryAdd(p.Id, 0);
            if (pendingPayments.Count > 0)
                logger.LogDebug("MintListener tracking {Count} pending melt payments", pendingPayments.Count);

            if (quotes.Count == 0)
                return;

            logger.LogDebug("MintListener recovering {Count} quotes", quotes.Count);

            foreach (var quote in quotes)
            {
                try
                {
                    switch (quote.QuoteState)
                    {
                        case "PAID":
                            await MintAndSaveProofsAsync(db, quote, ct);
                            break;

                        case "ISSUED":
                            await RecoverProofsAsync(db, quote, ct);
                            break;

                        default:
                            await SubscribeQuoteAsync(quote.Mint, quote.QuoteId, ct);
                            break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Recovery failed for quote {QuoteId}, will retry next startup",
                        quote.QuoteId);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MintListener recovery failed");
        }
    }


    private async Task ReadSubscriptionAsync(string key, Subscription subscription, CancellationToken ct)
    {
        var reachedTerminalState = false;
        try
        {
            await foreach (var msg in subscription.ReadAllAsync(ct))
            {
                if (msg is not WsMessage.Notification notification)
                    continue;

                reachedTerminalState = await HandleNotificationAsync(key, notification.Value, ct);
                if (reachedTerminalState)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error reading subscription {Key}", key);
        }
        finally
        {
            if (_activeSubscriptions.TryRemove(key, out var entry))
                entry.Cts.Dispose();

            await subscription.DisposeAsync();

            var mintUrl = key.Split('|', 2)[0];
            if (!_activeSubscriptions.Keys.Any(k => k.StartsWith(mintUrl)))
                await _wsService.DisconnectAsync(mintUrl, CancellationToken.None);

            if (!reachedTerminalState && !ct.IsCancellationRequested)
            {
                logger.LogTrace("Re-subscribing to {Key} after connection loss", key);
                await SubscribeQuoteAsync(mintUrl, key.Split('|', 2)[1], ct);
            }
        }
    }

    /// <returns>true if the quote reached a terminal state and the subscription should end</returns>
    private async Task<bool> HandleNotificationAsync(string key, WsNotification notification, CancellationToken ct)
    {
        try
        {
            var payload = NotificationParser.ParsePayload<PostMintQuoteBolt11Response>(notification);
            if (payload is null || string.IsNullOrEmpty(payload.Quote))
                return false;

            var state = payload.State;
            var quoteId = payload.Quote;

            // UNPAID is the initial status sent by the mint on every subscribe - nothing to do.
            if (state is "UNPAID")
                return false;

            await using var db = dbContextFactory.CreateContext();
            var payment = await db.LightningInvoices
                .FirstOrDefaultAsync(p => p.QuoteId == quoteId, ct);

            if (payment is null)
            {
                logger.LogDebug("Notification for unknown quote {QuoteId}", quoteId);
                return false;
            }

            if (payment.QuoteState is "ISSUED" && payment.Proofs is not null)
                return true;

            payment.QuoteState = state!;
            await db.SaveChangesAsync(ct);

            if (state is "PAID")
            {
                var minted = await MintAndSaveProofsAsync(db, payment, ct);
                if (minted)
                    NotifyListeners(payment);
                else
                {
                    // Minting failed - don't treat as terminal so the subscription stays alive
                    // and we can retry on the next notification or periodic cleanup picks it up.
                    logger.LogWarning("Minting failed for quote {QuoteId}, keeping subscription alive", quoteId);
                    return false;
                }
            }

            return state is "ISSUED";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error handling notification for {Key}", key);
            return false;
        }
    }


    /// <summary>
    /// mint tokens for a PAID quote.
    /// on success: sets ISSUED + saves proofs.
    /// on failure: state stays PAID — recovery will retry on next startup.
    /// </summary>
    /// <returns>true if proofs were minted and saved</returns>
    private async Task<bool> MintAndSaveProofsAsync(
        CashuDbContext db, CashuLightningClientInvoice payment, CancellationToken ct)
    {
        if (!_mintingQuotes.TryAdd(payment.QuoteId, 0))
        {
            logger.LogDebug("Quote {QuoteId} is already being minted", payment.QuoteId);
            return false;
        }

        try
        {
            const int maxAttempts = 3;
            var delay = TimeSpan.FromSeconds(2);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var wallet = Wallet.Create().WithMint(payment.Mint);
                    var req = new PostMintRequest
                    {
                        Outputs = payment.OutputData.Select(o => o.BlindedMessage).ToArray(),
                        Quote = payment.QuoteId,
                    };

                    var client = await wallet.GetMintApi(ct);
                    var promises = await client.Mint<PostMintRequest, PostMintResponse>("bolt11", req, ct);
                    var keys = await wallet.GetKeys(payment.KeysetId, true, false, ct);

                    if (keys is null)
                        throw new InvalidOperationException($"Keyset not found: {payment.KeysetId}");

                    var proofs = Utils.ConstructProofsFromPromises(
                        promises.Signatures.ToList(), payment.OutputData, keys.Keys);

                    payment.QuoteState = "ISSUED";
                    payment.PaidAt ??= DateTimeOffset.UtcNow;
                    payment.Proofs = StoredProof
                        .FromBatch(proofs, payment.StoreId, ProofState.Available).ToList();
                    await db.SaveChangesAsync(ct);

                    logger.LogDebug("Minted and saved proofs for quote {QuoteId}", payment.QuoteId);
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    logger.LogDebug(ex, "Mint attempt {Attempt}/{Max} failed for {QuoteId}, retrying",
                        attempt, maxAttempts, payment.QuoteId);
                    await Task.Delay(delay, ct);
                    delay *= 2;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to mint quote {QuoteId} after all retries", payment.QuoteId);
        }
        finally
        {
            _mintingQuotes.TryRemove(payment.QuoteId, out _);
        }

        return false;
    }

    /// <summary>
    /// recover proofs for an ISSUED quote where proofs weren't saved
    /// </summary>
    private async Task RecoverProofsAsync(
        CashuDbContext db, CashuLightningClientInvoice payment, CancellationToken ct)
    {
        using var api = CashuUtils.GetCashuHttpClient(payment.Mint);

        var req = new PostRestoreRequest
        {
            Outputs = payment.OutputData.Select(o => o.BlindedMessage).ToArray(),
        };
        var res = await api.Restore(req, ct);

        var returnedOutputs = new List<OutputData>();
        foreach (var output in res.Outputs)
        {
            var match = payment.OutputData.SingleOrDefault(o => Equals(o.BlindedMessage.B_, output.B_));
            if (match is not null)
                returnedOutputs.Add(match);
        }

        var keyset = await api.GetKeys(payment.KeysetId, ct);
        var keys = keyset.Keysets.SingleOrDefault()?.Keys
                   ?? throw new InvalidOperationException($"Keys not found: {payment.KeysetId}");

        var proofs = Utils.ConstructProofsFromPromises(res.Signatures, returnedOutputs, keys);

        payment.PaidAt ??= DateTimeOffset.UtcNow;
        payment.Proofs = StoredProof
            .FromBatch(proofs, payment.StoreId, ProofState.Available).ToList();
        await db.SaveChangesAsync(ct);

        logger.LogDebug("Recovered proofs for quote {QuoteId}", payment.QuoteId);
    }


    private async Task CleanupExpiredQuotesAsync()
    {
        if (!await _cleanupLock.WaitAsync(0))
            return;
        try
        {
            await using var db = dbContextFactory.CreateContext();
            var expired = await db.LightningInvoices
                .Where(p => p.QuoteState == "UNPAID" && p.Expiry <= DateTimeOffset.UtcNow)
                .ToListAsync();

            if (expired.Count > 0)
            {
                foreach (var payment in expired)
                {
                    payment.QuoteState = "EXPIRED";

                    var key = $"{payment.Mint}|{payment.QuoteId}";
                    if (_activeSubscriptions.TryGetValue(key, out var entry))
                        await entry.Cts.CancelAsync();
                }

                await db.SaveChangesAsync();
                logger.LogDebug("Cleaned up {Count} expired quotes", expired.Count);
            }

            await RetryStuckPaidQuotesAsync();
            await ResubscribeOrphanedQuotesAsync(db);
            await CheckPendingPaymentsAsync();

            foreach (var connection in _wsService.GetConnections().ToList())
            {
                if (!_wsService.GetSubscriptions(connection.MintUrl).Any())
                {
                    logger.LogDebug("Disconnecting idle connection to {Mint}", connection.MintUrl);
                    await _wsService.DisconnectAsync(connection.MintUrl);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error during periodic cleanup");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }


    private async Task RetryStuckPaidQuotesAsync()
    {
        try
        {
            await using var db = dbContextFactory.CreateContext();
            var stuckPaid = await db.LightningInvoices
                .Where(p => p.QuoteState == "PAID")
                .ToListAsync();

            foreach (var payment in stuckPaid)
            {
                try
                {
                    var minted = await MintAndSaveProofsAsync(db, payment, _cts?.Token ?? CancellationToken.None);
                    if (minted)
                    {
                        NotifyListeners(payment);
                        logger.LogDebug("Retried and minted stuck quote {QuoteId}", payment.QuoteId);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Retry failed for stuck quote {QuoteId}", payment.QuoteId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error retrying stuck PAID quotes");
        }
    }

    /// <summary>
    /// Re-subscribes via WS any UNPAID quotes that lost their subscription.
    /// The mint sends the current state as the first notification (NUT-17),
    /// so a re-subscribe on an already-PAID quote will trigger minting.
    /// </summary>
    private async Task ResubscribeOrphanedQuotesAsync(CashuDbContext db)
    {
        try
        {
            var unpaid = await db.LightningInvoices
                .Where(p => p.QuoteState == "UNPAID" && p.Expiry > DateTimeOffset.UtcNow)
                .ToListAsync();

            var activeKeys = _activeSubscriptions.Keys.ToHashSet();
            var orphaned = unpaid.Where(p => !activeKeys.Contains($"{p.Mint}|{p.QuoteId}")).ToList();

            if (orphaned.Count > 0)
                logger.LogDebug("Re-subscribing {Count} orphaned UNPAID quotes", orphaned.Count);

            foreach (var payment in orphaned)
                await SubscribeQuoteAsync(payment.Mint, payment.QuoteId, _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error re-subscribing orphaned quotes");
        }
    }

    private async Task CheckPendingPaymentsAsync()
    {
        foreach (var paymentId in _pendingPayments.Keys.ToList())
        {
            try
            {
                await using var db = dbContextFactory.CreateContext();
                var payment = await db.LightningPayments
                    .Include(p => p.Proofs)
                    .FirstOrDefaultAsync(p => p.Id == paymentId);

                if (payment is null || payment.QuoteState != "PENDING")
                {
                    _pendingPayments.TryRemove(paymentId, out _);
                    continue;
                }

                using var api = CashuUtils.GetCashuHttpClient(payment.Mint);
                var quote = await api.CheckMeltQuote<PostMeltQuoteBolt11Response>("bolt11", payment.QuoteId);

                switch (quote.State)
                {
                    case "PAID":
                        payment.QuoteState = "PAID";
                        payment.PaidAt = DateTimeOffset.UtcNow;
                        payment.Preimage = quote.PaymentPreimage ?? string.Empty;
                        foreach (var proof in payment.Proofs.Where(p => p.Status == ProofState.Reserved))
                            proof.Status = ProofState.Spent;
                        if (payment.BlankOutputs is { Count: > 0 })
                            await RestoreChangeProofsAsync(db, api, payment);
                        await db.SaveChangesAsync();
                        _pendingPayments.TryRemove(paymentId, out _);
                        logger.LogDebug("Finalized pending payment {PaymentId} as PAID", paymentId);
                        break;

                    case "UNPAID":
                    case "EXPIRED":
                        payment.QuoteState = quote.State!;
                        var reservedProofs = payment.Proofs
                            .Where(p => p.Status == ProofState.Reserved).ToList();
                        if (reservedProofs.Count > 0)
                        {
                            var checkReq = new PostCheckStateRequest
                            {
                                Ys = reservedProofs.Select(p => p.C.ToString()).ToArray()
                            };
                            var checkRes = await api.CheckState(checkReq);
                            var stateMap = checkRes.States.ToDictionary(s => s.Y, s => s.State);
                            foreach (var proof in reservedProofs)
                            {
                                var mintState = stateMap.GetValueOrDefault(proof.C.ToString());
                                proof.Status = mintState == StateResponseItem.TokenState.SPENT
                                    ? ProofState.Spent
                                    : ProofState.Available;
                            }
                        }
                        await db.SaveChangesAsync();
                        _pendingPayments.TryRemove(paymentId, out _);
                        logger.LogDebug("Rolled back payment {PaymentId}, quote {State}", paymentId, quote.State);
                        break;

                        // "PENDING": payment still in flight
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error checking pending payment {PaymentId}", paymentId);
            }
        }
    }

    private async Task RestoreChangeProofsAsync(
        CashuDbContext db, ICashuApi api, CashuLightningClientPayment payment)
    {
        try
        {
            var req = new PostRestoreRequest
            {
                Outputs = payment.BlankOutputs!.Select(o => o.BlindedMessage).ToArray(),
            };
            var res = await api.Restore(req);
            if (res.Signatures.Length == 0)
                return;

            var returnedOutputs = new List<OutputData>();
            foreach (var output in res.Outputs)
            {
                var match = payment.BlankOutputs.SingleOrDefault(o => Equals(o.BlindedMessage.B_, output.B_));
                if (match is not null)
                    returnedOutputs.Add(match);
            }

            var changeProofs = new List<Proof>();
            foreach (var keysetId in res.Signatures.Select(s => s.Id).Distinct())
            {
                var keyset = await api.GetKeys(keysetId);
                var keys = keyset.Keysets.SingleOrDefault()?.Keys;
                if (keys is null) continue;
                changeProofs.AddRange(Utils.ConstructProofsFromPromises(res.Signatures.ToList(), returnedOutputs, keys));
            }

            if (changeProofs.Count > 0)
            {
                db.Proofs.AddRange(StoredProof.FromBatch(changeProofs, payment.StoreId, ProofState.Available));
                logger.LogDebug("Restored {Count} change proof(s) for payment {PaymentId}", changeProofs.Count, payment.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to restore change proofs for payment {PaymentId}", payment.Id);
        }
    }

    private void NotifyListeners(CashuLightningClientInvoice payment)
    {
        var invoice = payment.ToLightningInvoice();
        foreach (var listener in _listeners.Values)
        {
            if (listener.StoreId == payment.StoreId && listener.MintUrl == payment.Mint)
                listener.NotificationChannel.Writer.TryWrite(invoice);
        }
    }

    public void Dispose()
    {
        _expiryTimer?.Dispose();
        _cts?.Dispose();
        _cleanupLock.Dispose();
    }

}

public class CashuListenerRegistration
{
    public required string Id { get; init; }
    public required string StoreId { get; init; }
    public required string MintUrl { get; init; }
    public required Channel<LightningInvoice> NotificationChannel { get; init; }
}
