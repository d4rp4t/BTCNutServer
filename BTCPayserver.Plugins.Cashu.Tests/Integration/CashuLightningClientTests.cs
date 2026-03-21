using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Lightning;
using BTCPayserver.Plugins.Cashu.Tests.Unit;
using BTCPayServer.Tests;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;
using Mnemonic = DotNut.NBitcoin.BIP39.Mnemonic;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuLightningClientTests(ITestOutputHelper helper)
{
    private static readonly Network TestNetwork = Network.RegTest;

    private string CdkMintUrl =>
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    private string CustomerLndUrl =>
        (
            Environment.GetEnvironmentVariable("TEST_CUSTOMERLND") ?? "http://localhost:35532"
        ).TrimEnd('/');

    private string MerchantLndUrl =>
        (
            Environment.GetEnvironmentVariable("TEST_MERCHANTLND") ?? "http://localhost:35531"
        ).TrimEnd('/');

    private const string StoreId = "integration-test-store";

    [Fact]
    public async Task CreateInvoice_ReturnsValidBolt11()
    {
        var (client, _) = await SetupClient();

        var invoice = await client.CreateInvoice(
            LightMoney.Satoshis(100),
            "test invoice",
            TimeSpan.FromMinutes(10)
        );

        helper.WriteLine($"Invoice ID: {invoice.Id}");
        helper.WriteLine($"BOLT11: {invoice.BOLT11}");

        Assert.NotNull(invoice);
        Assert.NotNull(invoice.Id);
        Assert.NotNull(invoice.BOLT11);
        Assert.Equal(LightMoney.Satoshis(100), invoice.Amount);
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
    }

    [Fact]
    public async Task CreateInvoice_PayViaBolt11_ListenerNotifies()
    {
        var (client, listener) = await SetupClient();

        var invoice = await client.CreateInvoice(
            LightMoney.Satoshis(100),
            "listen test",
            TimeSpan.FromMinutes(10)
        );
        helper.WriteLine($"Created invoice: {invoice.Id}, BOLT11: {invoice.BOLT11}");

        // Start listening before paying
        var invoiceListener = await client.Listen();

        // Pay the BOLT11 via customer_lnd
        await PayBolt11ViaLnd(invoice.BOLT11, CustomerLndUrl);

        // Wait for the listener to notify
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paid = await invoiceListener.WaitInvoice(cts.Token);

        helper.WriteLine($"Listener notified: {paid.Id}, status: {paid.Status}");
        Assert.Equal(invoice.Id, paid.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, paid.Status);
    }

    [Fact]
    public async Task GetInvoice_AfterCreate_ReturnsInvoice()
    {
        var (client, _) = await SetupClient();

        var created = await client.CreateInvoice(
            LightMoney.Satoshis(50),
            "get test",
            TimeSpan.FromMinutes(10)
        );

        var fetched = await client.GetInvoice(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.Amount, fetched.Amount);
    }

    [Fact]
    public async Task GetBalance_AfterMintAndListen_ReturnsPositiveBalance()
    {
        var (client, _) = await SetupClient();

        // Create invoice and pay it so proofs land in the wallet
        var invoice = await client.CreateInvoice(
            LightMoney.Satoshis(200),
            "balance test",
            TimeSpan.FromMinutes(10)
        );
        await PayBolt11ViaLnd(invoice.BOLT11, CustomerLndUrl);

        // Wait for mint to process and proofs to be saved
        var invoiceListener = await client.Listen();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paid = await invoiceListener.WaitInvoice(cts.Token);
        Assert.Equal(LightningInvoiceStatus.Paid, paid.Status);

        var balance = await client.GetBalance();
        helper.WriteLine(
            $"Balance: {balance.OffchainBalance.Local.ToUnit(LightMoneyUnit.Satoshi)} sat"
        );

        Assert.True(
            balance.OffchainBalance.Local > LightMoney.Zero,
            "Expected positive balance after minting"
        );
    }

    [Fact]
    public async Task Pay_WithSufficientBalance_Succeeds()
    {
        var (client, _) = await SetupClient();

        // Start listening BEFORE paying so we don't miss notifications
        var invoiceListener = await client.Listen();

        // Fund the wallet: create invoice + pay it
        var invoice = await client.CreateInvoice(
            LightMoney.Satoshis(1000),
            "fund wallet",
            TimeSpan.FromMinutes(10)
        );
        await PayBolt11ViaLnd(invoice.BOLT11, CustomerLndUrl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paid = await invoiceListener.WaitInvoice(cts.Token);
        helper.WriteLine($"Funded: {paid.Id}, status: {paid.Status}");

        // Check balance before paying
        var balance = await client.GetBalance();
        helper.WriteLine(
            $"Balance before pay: {balance.OffchainBalance.Local.ToUnit(LightMoneyUnit.Satoshi)} sat"
        );

        // Create a small BOLT11 on merchant_lnd to pay TO
        var targetBolt11 = await CreateBolt11OnLnd(10, MerchantLndUrl);
        helper.WriteLine($"Paying BOLT11: {targetBolt11}");

        var payResponse = await client.Pay(targetBolt11);

        helper.WriteLine(
            $"Pay result: {payResponse.Result}, fee: {payResponse.Details?.FeeAmount}"
        );
        Assert.Equal(PayResult.Ok, payResponse.Result);
    }

    [Fact]
    public async Task ListInvoices_ReturnsCreatedInvoices()
    {
        var (client, _) = await SetupClient();

        await client.CreateInvoice(LightMoney.Satoshis(10), "list1", TimeSpan.FromMinutes(10));
        await client.CreateInvoice(LightMoney.Satoshis(20), "list2", TimeSpan.FromMinutes(10));

        var invoices = await client.ListInvoices();
        helper.WriteLine($"Listed {invoices.Length} invoices");

        Assert.True(invoices.Length >= 2);
    }

    [Fact]
    public async Task ReceiveOnly_CreateInvoiceSucceeds_PayFails()
    {
        var (client, _) = await SetupClient(includeSecret: false);

        // CreateInvoice should work without secret (receive-only)
        var invoice = await client.CreateInvoice(
            LightMoney.Satoshis(100),
            "receive-only test",
            TimeSpan.FromMinutes(10)
        );

        Assert.NotNull(invoice);
        Assert.NotNull(invoice.BOLT11);
        helper.WriteLine($"Receive-only invoice: {invoice.Id}");

        // Pay should fail without secret
        var targetBolt11 = await CreateBolt11OnLnd(10, MerchantLndUrl);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Pay(targetBolt11)
        );
        helper.WriteLine($"Expected error: {ex.Message}");
        Assert.Contains("secret", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAndReceive_CreateInvoiceAndPayBothSucceed()
    {
        var (client, _) = await SetupClient(includeSecret: true);

        // CreateInvoice works with secret
        var invoice = await client.CreateInvoice(
            LightMoney.Satoshis(100),
            "send+receive test",
            TimeSpan.FromMinutes(10)
        );
        Assert.NotNull(invoice);
        Assert.NotNull(invoice.BOLT11);
        helper.WriteLine($"Send+receive invoice: {invoice.Id}");

        // Fund the wallet
        var invoiceListener = await client.Listen();
        var fundInvoice = await client.CreateInvoice(
            LightMoney.Satoshis(1000),
            "fund",
            TimeSpan.FromMinutes(10)
        );
        await PayBolt11ViaLnd(fundInvoice.BOLT11, CustomerLndUrl);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await invoiceListener.WaitInvoice(cts.Token);

        // Pay should succeed with secret
        var targetBolt11 = await CreateBolt11OnLnd(10, MerchantLndUrl);
        var payResponse = await client.Pay(targetBolt11);

        helper.WriteLine($"Pay result: {payResponse.Result}");
        Assert.Equal(PayResult.Ok, payResponse.Result);
    }

    private async Task<(CashuLightningClient client, MintListener listener)> SetupClient(
        bool includeSecret = true
    )
    {
        // DbCounter uses raw SQL (Dapper), so we need a real PostgreSQL database
        var pgConn =
            Environment.GetEnvironmentVariable("TESTS_POSTGRES")
            ?? "User ID=postgres;Host=localhost;Port=39372;Database=btcpayserver";
        Environment.SetEnvironmentVariable("TESTS_POSTGRES", pgConn);

        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener();
        await listener.StartAsync(CancellationToken.None);

        var mnemonic = new Mnemonic(
            new NBitcoin.Mnemonic(NBitcoin.Wordlist.English, WordCount.Twelve).ToString()
        );
        var secret = Guid.NewGuid();

        await using var ctx = db.CreateContext();
        ctx.CashuWalletConfig.Add(
            new CashuWalletConfig
            {
                StoreId = StoreId,
                WalletMnemonic = mnemonic,
                Verified = true,
                LightningClientSecret = secret,
            }
        );
        await ctx.SaveChangesAsync();

        var client = new CashuLightningClient(
            new Uri(CdkMintUrl),
            StoreId,
            includeSecret ? secret.ToString() : null,
            db,
            listener,
            db.CreateMintManager(),
            TestNetwork
        );

        return (client, listener);
    }

    private async Task PayBolt11ViaLnd(string bolt11, string lndUrl)
    {
        using var http = new HttpClient();
        var body = System.Text.Json.JsonSerializer.Serialize(new { payment_request = bolt11 });
        var resp = await http.PostAsync(
            $"{lndUrl}/v1/channels/transactions",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        );
        resp.EnsureSuccessStatusCode();
        helper.WriteLine($"Paid BOLT11 via LND at {lndUrl}");
    }

    private async Task<string> CreateBolt11OnLnd(long amountSat, string lndUrl)
    {
        using var http = new HttpClient();
        var body = System.Text.Json.JsonSerializer.Serialize(new { value = amountSat });
        var resp = await http.PostAsync(
            $"{lndUrl}/v1/invoices",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        );
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("payment_request").GetString()!;
    }
}
