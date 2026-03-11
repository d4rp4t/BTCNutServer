#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using DotNut.Abstractions;
using DotNut.Abstractions.Handlers;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace BTCPayServer.Plugins.Cashu.Lightning;

public class CashuLightningClient(
    Uri mintUrl,
    string storeId,
    string? secret,
    CashuDbContextFactory dbContextFactory,
    MintListener mintListener,
    Network network)
    : ILightningClient
{
    public string DisplayName => "Cashu";
    
    public override string ToString()
    {
        return secret is null
            ? $"type=cashu;mint-url={mintUrl};store-id={storeId};"
            : $"type=cashu;mint-url={mintUrl};store-id={storeId};secret={secret};";
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        
        await using var db = dbContextFactory.CreateContext();
        var walletConfig = db.CashuWalletConfig.Single(w => w.StoreId == storeId);
        using var wallet = Wallet
            .Create()
            .WithMint(mintUrl)
            .WithMnemonic(walletConfig.WalletMnemonic)
            .WithCounter(new DbCounter(dbContextFactory, storeId));
        
        var satAmount =
            decimal.ToUInt64(
                decimal.Round(
                    amount.ToUnit(LightMoneyUnit.Satoshi),
                    MidpointRounding.AwayFromZero
                )
            );
        
        var mintHandler = await wallet
            .CreateMintQuote()
            .WithAmount(satAmount)
            .WithUnit("sat")
            .WithDescription(description)
            .ProcessAsyncBolt11(cancellation);
        var quote = mintHandler.GetQuote();

        if (quote.Amount.HasValue && quote.Amount.Value != satAmount)
            throw new InvalidOperationException(
                $"Mint returned mismatched amount: requested {satAmount} sat, got {quote.Amount.Value} sat");

        if (!BOLT11PaymentRequest.TryParse(quote.Request, out var bolt11, network))
        {
            throw new InvalidOperationException($"Failed to parse BOLT11 from mint quote: {quote.Request}");
        }

        var invoiceId = bolt11!.PaymentHash?.ToString()
            ?? throw new Exception("BOLT11 missing payment hash");

        var outputs = mintHandler.GetOutputs();
        var invoice = new CashuLightningClientInvoice
        {
            StoreId = storeId,
            Mint = mintUrl.ToString().TrimEnd('/'),
            QuoteId = quote.Quote,
            InvoiceId = invoiceId,
            KeysetId = outputs[0].BlindedMessage.Id,
            Amount = quote.Amount.HasValue ? LightMoney.Satoshis((long)quote.Amount.Value) : amount,
            Bolt11 = quote.Request,
            QuoteState = quote.State ?? "UNPAID",
            Created = DateTimeOffset.UtcNow,
            Expiry = quote.Expiry is not null
                ? DateTimeOffset.FromUnixTimeSeconds(quote.Expiry.Value)
                : bolt11.ExpiryDate,
            OutputData = outputs
        };

        db.LightningInvoices.Add(invoice);
        await db.SaveChangesAsync(cancellation);

        // Fire-and-forget: WS subscription is a real-time optimization, not required for invoice creation.
        // Using CancellationToken.None so BTCPay's 5s timeout doesn't kill the WS handshake.
        _ = mintListener.SubscribeQuoteAsync(invoice.Mint, invoice.QuoteId, CancellationToken.None);

        return invoice.ToLightningInvoice();
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        return await CreateInvoice(createInvoiceRequest.Amount, createInvoiceRequest.Description,
            createInvoiceRequest.Expiry, cancellation);
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId,
        CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var payment = await db.LightningInvoices
            .FirstOrDefaultAsync(p => p.InvoiceId == invoiceId && p.StoreId == storeId, cancellation);

        return payment?.ToLightningInvoice();
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = default)
    {
        return await GetInvoice(paymentHash.ToString(), cancellation);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        var listener = new CashuListener(mintListener, storeId, mintUrl.ToString().TrimEnd('/'));
        return Task.FromResult<ILightningInvoiceListener>(listener);
    }

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = default)
    {
        await using var db =  dbContextFactory.CreateContext();
        var invoices = db.LightningInvoices
            .Where(s => s.StoreId == storeId);
        
        // it seems like blink plugin also doesn't support offset

        if (request.PendingOnly is true)
        {
            invoices = invoices.Where(i =>
                i.QuoteState == "PAID" ||
                (i.QuoteState == "UNPAID" && i.Expiry > DateTimeOffset.UtcNow));
        }

        var invoiceList = await invoices.ToListAsync(cancellation);
        return invoiceList.Select(i => i.ToLightningInvoice()).ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash,
        CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var payment = await db.LightningPayments
            .FirstOrDefaultAsync(p => p.PaymentHash == paymentHash && p.StoreId == storeId, cancellation);
        return payment?.ToLightningPayment();
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var payments = db.LightningPayments.Where(p => p.StoreId == storeId);

        if (request.IncludePending is false)
            payments = payments.Where(p => p.QuoteState == "PAID");

        var paymentList = await payments.ToListAsync(cancellation);
        return paymentList.Select(p => p.ToLightningPayment()).ToArray();
    }



    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var wallet = Wallet.Create().WithMint(mintUrl);
        var keysets = await wallet.GetKeysets(false, cancellation);
        var keysetIdStrings = keysets.Select(k => k.Id).ToList();
        var amount = db.Proofs
            .Where(p => p.StoreId == storeId
                        && p.Status == ProofState.Available
                        && keysetIdStrings.Contains(p.Id))
            .Select(p => p.Amount).AsEnumerable()
            .Sum();

        return new LightningNodeBalance
        {
            OffchainBalance = new OffchainBalance
            {
                Local = LightMoney.Satoshis(amount)
            }
        };
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams,
        CancellationToken cancellation = default)
    {
        // blink plugin does the same
        return Pay(null, payParams, cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = default)
    {
        var payLock = mintListener.GetPayLock(storeId);
        await payLock.WaitAsync(cancellation);
        try
        {
            return await PayLockedAsync(bolt11, cancellation);
        }
        finally
        {
            payLock.Release();
        }
    }

    private async Task<PayResponse> PayLockedAsync(string bolt11, CancellationToken cancellation)
    {
        await using var db = dbContextFactory.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(cancellation);
        var config = db.CashuWalletConfig.SingleOrDefault(w => w.StoreId == storeId);
        if (config == null)
            throw new InvalidOperationException($"Could not fetch cashu wallet config for storeId: {storeId}");
        if (secret == null || !Guid.TryParse(secret, out var parsed) || parsed != config.LightningClientSecret)
        {
            throw new InvalidOperationException("Invalid or null lightning client secret!");
        }
        // create melt quote 
        using var wallet = Wallet
            .Create()
            .WithMint(mintUrl)
            .WithMnemonic(config.WalletMnemonic)
            .WithCounter(new DbCounter(dbContextFactory, storeId));

        var handler = await wallet
            .CreateMeltQuote()
            .WithInvoice(bolt11)
            .ProcessAsyncBolt11(cancellation);
        var quote = handler.GetQuote();

        if (!BOLT11PaymentRequest.TryParse(bolt11, out var bolt11Parsed, network))
            throw new Exception($"Failed to parse BOLT11: {bolt11}");
        var paymentHash = bolt11Parsed!.PaymentHash?.ToString()
            ?? throw new Exception("BOLT11 missing payment hash");

        // save payment record before touching proofs
        var blankOutputs = handler is MeltHandlerBolt11 h ? h.GetBlankOutputs() : null;
        var payment = new CashuLightningClientPayment
        {
            StoreId = storeId,
            Mint = mintUrl.ToString().TrimEnd('/'),
            QuoteId = quote.Quote,
            QuoteState = "PENDING",
            PaymentHash = paymentHash,
            Bolt11 = bolt11,
            Amount = LightMoney.Satoshis((long)quote.Amount),
            CreatedAt = DateTimeOffset.UtcNow,
            BlankOutputs = blankOutputs,
        };
        db.LightningPayments.Add(payment);
        await db.SaveChangesAsync(cancellation);
        mintListener.TrackPendingPayment(payment.Id);

        var keysets = await wallet.GetKeysets(false, cancellation);
        var keysetIds = keysets.Select(k => k.Id).ToHashSet();
        var dbProofs = db.Proofs
            .Where(p => p.StoreId == storeId && p.Status == ProofState.Available)
            .ToList()
            .Where(p => keysetIds.Contains(p.Id))
            .ToList();
        var dotNutProofs = dbProofs.Select(p => p.ToDotNutProof()).ToList();
        var proofMap = dbProofs
            .Zip(dotNutProofs, (dbProof, proof) => (dbProof, proof))
            .ToDictionary(x => x.proof, x => x.dbProof);

        var targetAmount = quote.Amount + (ulong)quote.FeeReserve;
        var sendResponse = await wallet.SelectProofsToSend(
            dotNutProofs, targetAmount, true, cancellation);
        var selectedTotal = sendResponse.Send.Aggregate(0UL, (acc, p) => acc + p.Amount);

        foreach (var proof in sendResponse.Send)
        {
            proofMap[proof].Status = ProofState.Reserved;
            proofMap[proof].CashuLightningClientPaymentId = payment.Id;
        }
        await db.SaveChangesAsync(cancellation);

        List<Proof> changeProofs;

        if (selectedTotal > targetAmount)
        {
            var denominationLoss = selectedTotal - targetAmount;
            var keysetFees = keysets.ToDictionary(k => k.Id, k => k.InputFee ?? 0UL);
            var swapFeeEstimate = sendResponse.Send.ComputeFee(keysetFees);

            if (denominationLoss > swapFeeEstimate)
            {
                var swapped = await wallet.Swap()
                    .FromInputs(sendResponse.Send)
                    .ProcessAsync(cancellation);

                var split = await wallet.SelectProofsToSend(swapped, targetAmount, false, cancellation);

                foreach (var proof in sendResponse.Send)
                    proofMap[proof].Status = ProofState.Spent;

                var toMeltStored = StoredProof
                    .FromBatch(split.Send, storeId, ProofState.Reserved, payment.Id).ToList();
                db.Proofs.AddRange(toMeltStored);
                if (split.Keep.Count > 0)
                    db.Proofs.AddRange(StoredProof.FromBatch(split.Keep, storeId, ProofState.Available));
                await db.SaveChangesAsync(cancellation);

                changeProofs = await handler.Melt(split.Send, cancellation);

                foreach (var stored in toMeltStored)
                    stored.Status = ProofState.Spent;
                if (changeProofs.Count > 0)
                    db.Proofs.AddRange(StoredProof.FromBatch(changeProofs, storeId, ProofState.Available));

                var meltFee = targetAmount
                              - changeProofs.Aggregate(0UL, (acc, p) => acc + p.Amount)
                              - quote.Amount;
                payment.QuoteState = "PAID";
                payment.PaidAt = DateTimeOffset.UtcNow;
                payment.FeeAmount = LightMoney.Satoshis((long)meltFee);
                await db.SaveChangesAsync(cancellation);
                await tx.CommitAsync(cancellation);

                return new PayResponse
                {
                    Result = PayResult.Ok,
                    Details = new() { FeeAmount = LightMoney.Satoshis((long)meltFee) }
                };
            }
        }

        changeProofs = await handler.Melt(sendResponse.Send, cancellation);

        foreach (var proof in sendResponse.Send)
            proofMap[proof].Status = ProofState.Spent;
        if (changeProofs.Count > 0)
            db.Proofs.AddRange(StoredProof.FromBatch(changeProofs, storeId, ProofState.Available));

        var fee = targetAmount
                  - changeProofs.Aggregate(0UL, (acc, p) => acc + p.Amount)
                  - quote.Amount;
        payment.QuoteState = "PAID";
        payment.PaidAt = DateTimeOffset.UtcNow;
        payment.FeeAmount = LightMoney.Satoshis((long)fee);
        await db.SaveChangesAsync(cancellation);
        await tx.CommitAsync(cancellation);

        return new PayResponse
        {
            Result = PayResult.Ok,
            Details = new() { FeeAmount = LightMoney.Satoshis((long)fee) }
        };
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        return Pay(bolt11, new PayInvoiceParams(), cancellation);
    }

    /*
     * ============= *
     * Not supported *
     * ============= *
     */
    
    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        // return Task.FromResult(new LightningNodeInformation
        // {
        //     Alias = $"Cashu ({mintUrl.Host})",
        //     Version = "cashu",
        // });
        throw new NotSupportedException();
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo,
        CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }
}
