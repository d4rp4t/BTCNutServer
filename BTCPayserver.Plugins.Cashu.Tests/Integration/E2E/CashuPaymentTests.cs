using System.Text.RegularExpressions;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Tests;
using DotNut;
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
    private readonly string TestMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

    private string CdkMintUrl =>
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    private string CustomerLndUrl =>
        (Environment.GetEnvironmentVariable("TEST_CUSTOMERLND") ?? "http://localhost:35532").TrimEnd('/');

    private string NutshellMintUrl =>
        Environment.GetEnvironmentVariable("TEST_NUTSHELL_MINT_URL") ?? "http://localhost:3339";

    // ── Store config ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CanEnableCashuPaymentMethod()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await SetupCashuWallet(s, storeId);
        await EnableCashuPayments(s, storeId);

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
        await SetupCashuWallet(s, storeId);
        await EnableCashuPayments(s, storeId);

        // Create an invoice
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Go to invoice checkout
        await s.GoToInvoiceCheckout(invoiceId);
        await s.Page.AssertNoError();

        // The Cashu payment method should appear in checkout
        // The checkout renders a payment tab when Cashu PM is enabled
        var content = await s.Page.ContentAsync();
        Assert.Contains("cashu", content, StringComparison.OrdinalIgnoreCase);
    }

    // ── Full mint → pay flow (requires channel setup) ────────────────────────

    [Fact]
    public async Task CanPayInTrustedMintsOnlyMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await SetupCashuWallet(s, storeId);
        await EnableCashuPayments(s, storeId);

        // Create a 1 USD invoice
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Mint 200 sat tokens via CDK mint (customer pays LN invoice → gets tokens)
        var token = await MintCashuTokenAsync(200);
        Assert.NotNull(token);

        // Verify token round-trips correctly before sending
        helper.WriteLine($"Token: {token}");
        Assert.True(CashuUtils.TryDecodeToken(token, out var decoded),
            $"Token failed local decode round-trip: {token}");
        helper.WriteLine($"Decoded token has {decoded!.Tokens.SelectMany(t => t.Proofs).Count()} proofs");

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
            new() { Timeout = 30_000 });
        await s.Page.Locator("#payButton").ClickAsync();
        var response = await responseTask;

        if ((int)response.Status >= 400)
        {
            var body = await response.TextAsync();
            Assert.Fail($"Payment request failed with status {response.Status}: {body}");
        }

        // Wait for redirect back to invoice after payment
        await s.Page.WaitForURLAsync(
            new Regex($"/i/{invoiceId}"),
            new() { Timeout = 30_000 });

        var content = await s.Page.ContentAsync();
        Assert.True(
            content.Contains("Paid", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("settled", StringComparison.OrdinalIgnoreCase),
            "Expected invoice to be settled after Cashu payment");

    }
    
    [Fact]
    public async Task RejectsUntrustedMintsInTrustedMintsOnlyMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await SetupCashuWallet(s, storeId);
        // Enable Cashu with CDK mint as trusted — nutshell mint is NOT trusted
        await EnableCashuPayments(s, storeId);

        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Mint tokens from nutshell (untrusted) mint
        var token = await MintCashuTokenAsync(200, NutshellMintUrl);
        Assert.NotNull(token);

        // Submit via HTTP — expect rejection
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var payUrl = s.ServerUri.AbsoluteUri.TrimEnd('/') + "/cashu/pay-invoice";
        var payResp = await http.PostAsync(payUrl, new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("invoiceId", invoiceId),
        ]));
        var payBody = await payResp.Content.ReadAsStringAsync();

        helper.WriteLine($"RejectsUntrusted: status={payResp.StatusCode}, body={payBody}");
        Assert.True((int)payResp.StatusCode >= 400,
            $"Expected error response for untrusted mint, got {payResp.StatusCode}: {payBody}");
    }

    [Fact]
    public async Task CanPayInAutoConvertMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await SetupCashuWallet(s, storeId);
        await SetupLightningNode(s);
        await EnableCashuPayments(s, storeId, "AutoConvert");

        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Any mint works in AutoConvert — use CDK
        var token = await MintCashuTokenAsync(200);
        Assert.NotNull(token);

        await PayWithTokenViaCheckout(s, invoiceId, token);
    }

    [Fact]
    public async Task CanPayInHoldWhenTrustedMode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await SetupCashuWallet(s, storeId);
        await SetupLightningNode(s);
        // CDK mint is trusted, nutshell is not
        await EnableCashuPayments(s, storeId, "HoldWhenTrusted");

        // Pay from trusted mint — should swap (hold as ecash)
        var invoiceId1 = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var trustedToken = await MintCashuTokenAsync(200);
        await PayWithTokenViaCheckout(s, invoiceId1, trustedToken);

        // Pay from untrusted mint — should melt (convert to LN)
        var invoiceId2 = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var untrustedToken = await MintCashuTokenAsync(200, NutshellMintUrl);
        await PayWithTokenViaCheckout(s, invoiceId2, untrustedToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SetupCashuWallet(PlaywrightTester s, string storeId)
    {
        helper.WriteLine($"Using mnemonic: {TestMnemonic}");
        await s.GoToUrl($"/stores/{storeId}/cashu/getting-started");
        await s.Page.ClickAsync("#ImportWalletOptionsLink");

        var words = TestMnemonic.Split(' ');
        for (var i = 0; i < words.Length; i++)
            await s.Page.Locator($"#mnemonic-grid input[data-index='{i}']").FillAsync(words[i]);

        await s.Page.Locator("textarea[name='MintUrls']").FillAsync(CdkMintUrl);
        await s.Page.WaitForFunctionAsync("!document.getElementById('proceed').hasAttribute('disabled')");
        await s.Page.ClickAsync("#proceed");

        await s.Page.WaitForURLAsync(new Regex("restore-status"), new() { Timeout = 10_000 });

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await s.Page.Locator(".finish-btn").IsVisibleAsync())
            {
                await s.Page.ClickAsync(".finish-btn");
                return;
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException("Cashu wallet restore did not complete in time");
    }

    private async Task EnableCashuPayments(PlaywrightTester s, string storeId,
        string paymentMode = "TrustedMintsOnly")
    {
        // Step 1: Enable Cashu and save — the trusted mints section only renders when Enabled=true
        await s.GoToUrl($"/stores/{storeId}/cashu");
        var toggle = s.Page.Locator("input[id='Enabled']");
        if (!await toggle.IsCheckedAsync())
            await toggle.ClickAsync();
        await s.ClickPagePrimary();
        await s.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Success);

        // Step 2: Now the page re-renders with mode selector and trusted mints UI
        await s.GoToUrl($"/stores/{storeId}/cashu");

        // Select payment mode (radio buttons are visually-hidden, click via label)
        var modeId = paymentMode switch
        {
            "AutoConvert" => "autoConvert",
            "HoldWhenTrusted" => "holdWhenTrusted",
            _ => "trustedMintsOnly"
        };
        await s.Page.Locator($"label[for='{modeId}']").ClickAsync();

        // Add CDK mint as trusted (visible for TrustedMintsOnly and HoldWhenTrusted)
        if (paymentMode != "AutoConvert")
        {
            await s.Page.Locator("#mintUrl").FillAsync(CdkMintUrl);
            await s.Page.Locator("[data-action='add-mint']").ClickAsync();
        }

        await s.ClickPagePrimary();
        await s.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Success);
    }

    private string MerchantLndUrl =>
        (Environment.GetEnvironmentVariable("TEST_MERCHANTLND") ?? "http://localhost:35531/").TrimEnd('/');

    private async Task SetupLightningNode(PlaywrightTester s)
    {
        var connectionString = $"type=lnd-rest;server={MerchantLndUrl};allowinsecure=true";
        await s.GoToLightningSettings();
        await s.Page.ClickAsync("label[for=\"LightningNodeType-Custom\"]");
        await s.Page.FillAsync("#ConnectionString", connectionString);
        await s.ClickPagePrimary();
        await s.FindAlertMessage(partialText: "BTC Lightning node updated.");

        var enabled = await s.Page.WaitForSelectorAsync("#BTCLightningEnabled");
        if (!await enabled!.IsCheckedAsync())
        {
            await enabled.ClickAsync();
            await s.ClickPagePrimary();
            await s.FindAlertMessage(partialText: "BTC Lightning settings successfully updated");
        }
    }

    private async Task PayWithTokenViaCheckout(PlaywrightTester s, string invoiceId, string token)
    {
        await s.GoToInvoiceCheckout(invoiceId);
        await s.Page.AssertNoError();

        var cashuTab = s.Page.Locator(".payment-method", new() { HasText = "Cashu" });
        if (await cashuTab.IsVisibleAsync())
            await cashuTab.ClickAsync();

        await s.Page.WaitForSelectorAsync("input[name='token']", new() { Timeout = 15_000 });
        await s.Page.FillAsync("input[name='token']", token);

        // Listen for navigation response to detect server errors
        var responseTask = s.Page.WaitForResponseAsync(
            r => r.Url.Contains("cashu/pay-invoice"),
            new() { Timeout = 30_000 });
        await s.Page.Locator("#payButton").ClickAsync();
        var response = await responseTask;

        if ((int)response.Status >= 400)
        {
            var body = await response.TextAsync();
            Assert.Fail($"Payment request failed with status {response.Status}: {body}");
        }

        await s.Page.WaitForURLAsync(
            new Regex($"/i/{invoiceId}"),
            new() { Timeout = 30_000 });

        var content = await s.Page.ContentAsync();
        Assert.True(
            content.Contains("Paid", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("settled", StringComparison.OrdinalIgnoreCase),
            "Expected invoice to be settled after Cashu payment");
    }

    /// <summary>
    /// Mints Cashu tokens via CDK mint by paying a Lightning invoice through customer_lnd.
    /// Requires channel-setup to have run (customer_lnd → mint_lnd channel).
    /// </summary>
    private async Task<string> MintCashuTokenAsync(ulong amountSat, string? mintUrl = null)
    {
        mintUrl ??= CdkMintUrl;
        // Build a wallet against the mint using DotNut's fluent API
        var wallet = DotNut.Abstractions.Wallet.Create().WithMint(mintUrl);

        // Step 1: Create mint quote — wallet builds blinded messages internally
        var mintHandler = await wallet
            .CreateMintQuote()
            .WithAmount(amountSat)
            .WithUnit("sat")
            .ProcessAsyncBolt11();

        var quote = mintHandler.GetQuote();
        var bolt11 = quote.Request;

        // Step 2: Pay the LN invoice via customer_lnd REST (no macaroons)
        using var http = new HttpClient();
        var payBody = System.Text.Json.JsonSerializer.Serialize(new { payment_request = bolt11 });
        var payResp = await http.PostAsync(
            $"{CustomerLndUrl}/v1/channels/transactions",
            new StringContent(payBody, System.Text.Encoding.UTF8, "application/json"));
        payResp.EnsureSuccessStatusCode();

        // Allow time for LN payment to propagate
        await Task.Delay(2000);

        // Step 3: Exchange blinded messages for proofs
        var proofs = await mintHandler.Mint();

        // Step 4: Encode as cashuB token string
        var cashuToken = new CashuToken
        {
            Tokens = [new CashuToken.Token(mintUrl, proofs)], Unit="sat"
        };
        return cashuToken.Encode();
    }
}
