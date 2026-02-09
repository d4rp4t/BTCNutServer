using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using DotNut.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Cashu.Lightning;

public class CashuLightningClient : ILightningClient
{
    private readonly Uri _mintUrl;
    private readonly string _storeId;
    private readonly CashuDbContextFactory _dbContextFactory;
    private readonly MintListener _mintListener;
    private readonly Network _network;
    private readonly ILogger _logger;

    public CashuLightningClient(Uri mintUrl, string storeId, CashuDbContextFactory dbContextFactory,
        MintListener mintListener, Network network, ILogger logger)
    {
        _mintUrl = mintUrl;
        _storeId = storeId;
        _dbContextFactory = dbContextFactory;
        _mintListener = mintListener;
        _network = network;
        _logger = logger;
    }

    public override string ToString()
    {
        return $"type=cashu;mint-url={_mintUrl};store-id={_storeId};";
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        
        await using var db = _dbContextFactory.CreateContext(); 
        //todo make sure to secure it somehow. i don't feel like knowing storeID is a right way to authorize xD
        var walletConfig = db.CashuWalletConfig.Single(w => w.StoreId == _storeId);
        int i = 0;
        using var wallet = Wallet
            .Create()
            .WithMint(_mintUrl)
            .WithMnemonic(walletConfig.WalletMnemonic)
            .WithCounter(new DbCounter(_dbContextFactory, _storeId));
        
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

        if (!BOLT11PaymentRequest.TryParse(quote.Request, out var bolt11, _network))
        {
            throw new Exception($"Failed to parse BOLT11 from mint quote: {quote.Request}");
        }

        var invoiceId = bolt11.PaymentHash?.ToString()
            ?? throw new Exception("BOLT11 missing payment hash");

        var outputs = mintHandler.GetOutputs();
        var invoice = new CashuLightningClientInvoice
        {
            StoreId = _storeId,
            Mint = _mintUrl.ToString().TrimEnd('/'),
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

        await _mintListener.SubscribeQuoteAsync(
            invoice.Mint, invoice.QuoteId, cancellation);

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
        await using var db = _dbContextFactory.CreateContext();
        var payment = await db.LightningInvoices
            .FirstOrDefaultAsync(p => p.InvoiceId == invoiceId && p.StoreId == _storeId, cancellation);

        return payment?.ToLightningInvoice();
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = default)
    {
        return await GetInvoice(paymentHash.ToString(), cancellation);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        var listener = new CashuListener(_mintListener, _storeId, _mintUrl.ToString().TrimEnd('/'));
        return Task.FromResult<ILightningInvoiceListener>(listener);
    }

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = default)
    {
        await using var db =  _dbContextFactory.CreateContext();
        var invoices = db.LightningInvoices
            .Where(s => s.StoreId == _storeId);
        
        // it seems like blink plugin also doesn't support offset..

        if (request.PendingOnly is true)
        {
            invoices = invoices.Where(i => i.Status == LightningInvoiceStatus.Unpaid);
        }
        
        return invoices.Select(i => i.ToLightningInvoice()).ToArray();
    }

    public Task<LightningPayment> GetPayment(string paymentHash,
        CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return ListPayments(new ListPaymentsParams(), cancellation);
    }

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }



    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        await using var db = _dbContextFactory.CreateContext();
        var wallet = Wallet.Create().WithMint(_mintUrl);
        var keysets = await wallet.GetKeysets(false, cancellation);
        var keysetIds = keysets.Select(k => k.Id);
        var amount = db.Proofs
            .Where(p => p.StoreId == _storeId && keysetIds.Contains(p.Id))
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
        throw new NotImplementedException();
    }

    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    /*
     * ============= *
     * Not supported *
     * ============= *
     */
    
    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
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
