using System.Text.RegularExpressions;
using BTCPayServer.Tests;
using DotNut;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuPaymentTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private static readonly string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private string CdkMintUrl =>
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    private string CustomerLndUrl =>
        (Environment.GetEnvironmentVariable("TEST_CUSTOMERLND") ?? "http://localhost:35532").TrimEnd('/');

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

        // Navigate to store config
        await s.GoToUrl($"/stores/{storeId}/cashu");
        await s.Page.AssertNoError();

        // Enable Cashu and set trusted mints
        var toggle = s.Page.Locator("input[id='Enabled']");
        if (!await toggle.IsCheckedAsync())
            await toggle.ClickAsync();

        // Fill trusted mints URL
        var trustedMintsInput = s.Page.Locator("textarea[name='TrustedMintsUrls']");
        if (await trustedMintsInput.IsVisibleAsync())
            await trustedMintsInput.FillAsync(CdkMintUrl);

        await s.ClickPagePrimary();
        await s.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Success,
            "Config Saved Successfully");
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
    public async Task CanMintAndPayInvoiceWithCashuToken()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await SetupCashuWallet(s, storeId);
        await EnableCashuPayments(s, storeId);

        // Create a small invoice (100 sats)
        var invoiceId = await s.CreateInvoice(storeId, amount: null, currency: "BTC");
        Assert.NotNull(invoiceId);

        // Mint 200 sat tokens via CDK mint (customer pays LN invoice → gets tokens)
        var token = await MintCashuTokenAsync(200);
        Assert.NotNull(token);

        // Go to checkout and pay with token
        await s.GoToInvoiceCheckout(invoiceId);
        await s.Page.AssertNoError();

        // Wait for Cashu payment method to load in checkout
        await s.Page.WaitForSelectorAsync("input[name='token']", new() { Timeout = 10_000 });
        await s.Page.FillAsync("input[name='token']", token);
        await s.Page.Locator("#payByTokenForm").EvaluateAsync("f => f.submit()");

        // Wait for invoice to be marked as paid
        await s.Page.WaitForURLAsync(
            new Regex($"/i/{invoiceId}"),
            new() { Timeout = 30_000 });

        var content = await s.Page.ContentAsync();
        Assert.True(
            content.Contains("Paid", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("settled", StringComparison.OrdinalIgnoreCase),
            "Expected invoice to be settled after Cashu payment");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SetupCashuWallet(PlaywrightTester s, string storeId)
    {
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

    private async Task EnableCashuPayments(PlaywrightTester s, string storeId)
    {
        await s.GoToUrl($"/stores/{storeId}/cashu");

        var toggle = s.Page.Locator("input[id='Enabled']");
        if (!await toggle.IsCheckedAsync())
            await toggle.ClickAsync();

        var trustedMintsInput = s.Page.Locator("textarea[name='TrustedMintsUrls']");
        if (await trustedMintsInput.IsVisibleAsync())
            await trustedMintsInput.FillAsync(CdkMintUrl);

        await s.ClickPagePrimary();
        await s.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Success);
    }

    /// <summary>
    /// Mints Cashu tokens via CDK mint by paying a Lightning invoice through customer_lnd.
    /// Requires channel-setup to have run (customer_lnd → mint_lnd channel).
    /// </summary>
    private async Task<string> MintCashuTokenAsync(ulong amountSat)
    {
        // Build a wallet against the CDK mint using DotNut's fluent API
        var wallet = DotNut.Abstractions.Wallet.Create().WithMint(CdkMintUrl);

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

        // Step 4: Encode as cashuA token string
        var cashuToken = new CashuToken
        {
            Tokens = [new CashuToken.Token(CdkMintUrl, proofs)]
        };
        return cashuToken.Encode();
    }
}
