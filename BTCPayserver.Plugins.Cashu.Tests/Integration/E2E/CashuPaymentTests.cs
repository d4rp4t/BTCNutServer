using System.Text.RegularExpressions;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Tests;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuPaymentTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    // Use a fresh random mnemonic per test run to avoid "outputs already signed" errors
    // when mint containers retain state between runs (deterministic outputs collide on reuse).
    private readonly string TestMnemonic = new Mnemonic(
        Wordlist.English,
        WordCount.Twelve
    ).ToString();

    private string CdkMintUrl => PlaywrightTesterCashuUtils.GetCdkMintUrl();
    private string NutshellMintUrl => PlaywrightTesterCashuUtils.GetNutshellMintUrl();
    private string CustomerLndUrl => PlaywrightTesterCashuUtils.GetCustomerLndUrl();

    [Fact]
    public async Task CanEnableCashuPaymentMethod()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Verify config persisted — revisit page and check state
        await s.GoToUrl($"/stores/{storeId}/cashu");
        await s.Page.AssertNoError();

        var toggle = s.Page.Locator("input[id='Enabled']");
        Assert.True(await toggle.IsCheckedAsync(), "Cashu should be enabled");

        // Trusted mint should be listed
        var content = await s.Page.ContentAsync();
        Assert.Contains(CdkMintUrl, content);
    }

    [Fact]
    public async Task CashuCheckoutTabAppearsOnInvoice()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Create an invoice
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Go to invoice checkout
        await s.GoToInvoiceCheckout(invoiceId);
        await s.Page.AssertNoError();

        // The Cashu payment method should appear in checkout
        var content = await s.Page.ContentAsync();
        Assert.Contains("cashu", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CanPayInTrustedMintsOnlyMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Create a 1 USD invoice
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Mint 200 sat tokens via CDK mint (customer pays LN invoice → gets tokens)
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        Assert.NotNull(token);

        // Verify token round-trips correctly before sending
        helper.WriteLine($"Token: {token}");
        Assert.True(
            CashuUtils.TryDecodeToken(token, out var decoded),
            $"Token failed local decode round-trip: {token}"
        );
        helper.WriteLine(
            $"Decoded token has {decoded!.Tokens.SelectMany(t => t.Proofs).Count()} proofs"
        );

        // Go to checkout and pay with token
        await s.GoToInvoiceCheckout(invoiceId);
        await s.Page.AssertNoError();

        // Click the Cashu payment tab to make sure it is active
        var cashuTab = s.Page.Locator(".payment-method", new() { HasText = "Cashu" });
        if (await cashuTab.IsVisibleAsync())
            await cashuTab.ClickAsync();

        // Wait for Cashu payment method to load in checkout
        await s.Page.WaitForSelectorAsync("input[name='token']", new() { Timeout = 15_000 });
        await s.Page.FillAsync("input[name='token']", token);

        // Click the Pay button and verify response
        var responseTask = s.Page.WaitForResponseAsync(
            r => r.Url.Contains("cashu/pay-invoice"),
            new() { Timeout = 30_000 }
        );
        await s.Page.Locator("#payButton").ClickAsync();
        var response = await responseTask;

        if ((int)response.Status >= 400)
        {
            var body = await response.TextAsync();
            Assert.Fail($"Payment request failed with status {response.Status}: {body}");
        }

        // Wait for redirect back to invoice after payment
        await s.Page.WaitForURLAsync(new Regex($"/i/{invoiceId}"), new() { Timeout = 30_000 });

        var content = await s.Page.ContentAsync();
        Assert.True(
            content.Contains("Paid", StringComparison.OrdinalIgnoreCase)
                || content.Contains("settled", StringComparison.OrdinalIgnoreCase),
            "Expected invoice to be settled after Cashu payment"
        );
    }

    [Fact]
    public async Task RejectsUntrustedMintsInTrustedMintsOnlyMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        // Enable Cashu with CDK mint as trusted — nutshell mint is NOT trusted
        await s.EnableCashuPayments(storeId);

        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Mint tokens from nutshell (untrusted) mint
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200, NutshellMintUrl);
        Assert.NotNull(token);

        // Submit via HTTP — expect rejection
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var payUrl = s.ServerUri.AbsoluteUri.TrimEnd('/') + "/cashu/pay-invoice";
        var payResp = await http.PostAsync(
            payUrl,
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", token),
                new KeyValuePair<string, string>("invoiceId", invoiceId),
            ])
        );
        var payBody = await payResp.Content.ReadAsStringAsync();

        helper.WriteLine($"RejectsUntrusted: status={payResp.StatusCode}, body={payBody}");
        Assert.True(
            (int)payResp.StatusCode >= 400,
            $"Expected error response for untrusted mint, got {payResp.StatusCode}: {payBody}"
        );
    }

    [Fact]
    public async Task CanPayInAutoConvertMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.SetupLightningNode();
        await s.EnableCashuPayments(storeId, "AutoConvert");

        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Any mint works in AutoConvert — use CDK
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        Assert.NotNull(token);

        await s.PayWithTokenViaCheckout(invoiceId, token);
    }

    [Fact]
    public async Task CanPayInHoldWhenTrustedMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.SetupLightningNode();
        // CDK mint is trusted, nutshell is not
        await s.EnableCashuPayments(storeId, "HoldWhenTrusted");

        // Pay from trusted mint — should swap (hold as ecash)
        var invoiceId1 = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var trustedToken = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        await s.PayWithTokenViaCheckout(invoiceId1, trustedToken);

        // Pay from untrusted mint — should melt (convert to LN)
        var invoiceId2 = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var untrustedToken = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(
            200,
            NutshellMintUrl
        );
        await s.PayWithTokenViaCheckout(invoiceId2, untrustedToken);
    }

    [Fact]
    public async Task CanPayViaNut19PaymentRequest()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Mint tokens
        var mintUrl = CdkMintUrl;
        var wallet = DotNut.Abstractions.Wallet.Create().WithMint(mintUrl);

        var mintHandler = await wallet
            .CreateMintQuote()
            .WithAmount(200)
            .WithUnit("sat")
            .ProcessAsyncBolt11();

        var quote = mintHandler.GetQuote();

        using var http = new HttpClient();
        var payBody = System.Text.Json.JsonSerializer.Serialize(
            new { payment_request = quote.Request }
        );
        var payResp = await http.PostAsync(
            $"{CustomerLndUrl}/v1/channels/transactions",
            new StringContent(payBody, System.Text.Encoding.UTF8, "application/json")
        );
        payResp.EnsureSuccessStatusCode();
        await Task.Delay(2000);

        var proofs = await mintHandler.Mint();

        // Build NUT-19 payment request payload
        var payload = new
        {
            id = invoiceId,
            mint = mintUrl,
            unit = "sat",
            proofs = proofs,
        };

        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
        helper.WriteLine($"NUT-19 payload: {payloadJson[..Math.Min(200, payloadJson.Length)]}...");

        var prUrl = s.ServerUri.AbsoluteUri.TrimEnd('/') + "/cashu/pay-invoice-pr";
        var prResp = await http.PostAsync(
            prUrl,
            new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json")
        );

        var prBody = await prResp.Content.ReadAsStringAsync();
        helper.WriteLine($"NUT-19 response: {prResp.StatusCode}, body: {prBody}");

        Assert.True(
            prResp.IsSuccessStatusCode,
            $"NUT-19 payment failed: {prResp.StatusCode} — {prBody}"
        );
    }
}
