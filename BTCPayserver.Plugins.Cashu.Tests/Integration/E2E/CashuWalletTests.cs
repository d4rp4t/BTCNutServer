using System.Text.RegularExpressions;
using BTCPayServer.Tests;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuWalletTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private readonly string TestMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

    private string CdkMintUrl => PlaywrightTesterCashuUtils.GetCdkMintUrl();

    // ── Basic page loads ────────────────────────────────────────────────────

    [Fact]
    public async Task CashuWalletPageLoads()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);

        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.AssertNoError();

        Assert.Contains("cashu/wallet", s.Page.Url);
    }

    [Fact]
    public async Task CashuStoreConfigPageLoads()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);

        await s.GoToUrl($"/stores/{storeId}/cashu");
        await s.Page.AssertNoError();
    }

    // ── Empty state ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WalletShowsEmptyStateWhenNoFunds()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);

        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.AssertNoError();

        var content = await s.Page.ContentAsync();
        Assert.Contains("No funds available", content);
    }

    // ── Balance after payment ───────────────────────────────────────────────

    [Fact]
    public async Task WalletShowsBalanceAfterReceivingPayment()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Create invoice and pay with Cashu tokens
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        await s.PayWithTokenViaCheckout(invoiceId, token);

        // Navigate to wallet — should show balance
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.AssertNoError();

        // The balance table should have a row with amount > 0
        var balanceRow = s.Page.Locator("table.table tbody tr").First;
        var amountText = await balanceRow.Locator(".h5").TextContentAsync();
        helper.WriteLine($"Wallet balance: {amountText}");

        Assert.NotNull(amountText);
        Assert.True(ulong.TryParse(amountText.Trim(), out var amount) && amount > 0,
            $"Expected positive balance, got: {amountText}");
    }

    // ── Export token ────────────────────────────────────────────────────────

    [Fact]
    public async Task CanExportTokenFromWallet()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Fund the wallet
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        await s.PayWithTokenViaCheckout(invoiceId, token);

        // Go to wallet and click Export
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.AssertNoError();

        var exportBtn = s.Page.Locator("button[name='action'][value='SendToken']").First;
        Assert.True(await exportBtn.IsVisibleAsync(), "Export button should be visible when wallet has funds");
        await exportBtn.ClickAsync();

        // Should redirect to exported token page
        await s.Page.WaitForURLAsync(new Regex("cashu/token/"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();

        // Verify exported token page content
        var qrCanvas = s.Page.Locator("#qrcode");
        Assert.True(await qrCanvas.IsVisibleAsync(), "QR code should be visible");

        var copyBtn = s.Page.Locator("#copyTokenBtn");
        Assert.True(await copyBtn.IsVisibleAsync(), "Copy token button should be visible");

        // Token should be stored in hidden div
        var tokenValue = await s.Page.Locator("#token-dummy-div").GetAttributeAsync("data-token");
        helper.WriteLine($"Exported token: {tokenValue?[..Math.Min(60, tokenValue.Length)]}...");
        Assert.NotNull(tokenValue);
        Assert.StartsWith("cashu", tokenValue);

        // Amount should be displayed
        var amountDisplay = await s.Page.Locator(".amount-value").TextContentAsync();
        helper.WriteLine($"Exported amount: {amountDisplay}");
        Assert.NotNull(amountDisplay);

        // Mint URL should be displayed
        var mintUrl = await s.Page.Locator(".mint-url").TextContentAsync();
        Assert.NotNull(mintUrl);
        Assert.Contains("localhost", mintUrl);
    }

    // ── Token export history ────────────────────────────────────────────────

    [Fact]
    public async Task TokenExportHistoryShowsExportedTokens()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Fund the wallet
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        await s.PayWithTokenViaCheckout(invoiceId, token);

        // Export a token
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.Locator("button[name='action'][value='SendToken']").First.ClickAsync();
        await s.Page.WaitForURLAsync(new Regex("cashu/token/"), new() { Timeout = 10_000 });

        // Go back to wallet
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.AssertNoError();

        // Token Export History should show the exported token
        var content = await s.Page.ContentAsync();
        Assert.Contains("Pending", content);

        // Details link should be visible
        var detailsLink = s.Page.Locator("a.btn:has-text('Details')").First;
        Assert.True(await detailsLink.IsVisibleAsync(), "Details link should be visible in history");

        // Click Details and verify it navigates to token page
        await detailsLink.ClickAsync();
        await s.Page.WaitForURLAsync(new Regex("cashu/token/"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();

        Assert.True(await s.Page.Locator("#qrcode").IsVisibleAsync());
    }

    // ── Check token states ──────────────────────────────────────────────────

    [Fact]
    public async Task CanCheckTokenStates()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Fund wallet and export
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        await s.PayWithTokenViaCheckout(invoiceId, token);

        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.Locator("button[name='action'][value='SendToken']").First.ClickAsync();
        await s.Page.WaitForURLAsync(new Regex("cashu/token/"), new() { Timeout = 10_000 });

        // Go back and click refresh to check token states
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.AssertNoError();

        var refreshBtn = s.Page.Locator("#refreshButton");
        Assert.True(await refreshBtn.IsVisibleAsync(), "Refresh button should be visible");
        await refreshBtn.ClickAsync();

        // Should redirect back to wallet after checking states
        await s.Page.WaitForURLAsync(new Regex("cashu/wallet"), new() { Timeout = 15_000 });
        await s.Page.AssertNoError();
    }

    // ── Remove spent proofs ───────────────────────────────────────────────

    [Fact]
    public async Task CanRemoveSpentProofs()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);
        await s.EnableCashuPayments(storeId);

        // Fund wallet
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);
        await s.PayWithTokenViaCheckout(invoiceId, token);

        // Go to settings and click "Remove spent ecash"
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.AssertNoError();

        var removeBtn = s.Page.Locator("a", new() { HasText = "Remove spent ecash" });
        Assert.True(await removeBtn.IsVisibleAsync(), "Remove spent ecash button should be visible");
        await removeBtn.ClickAsync();

        // Should redirect back to wallet with a status message
        await s.Page.WaitForURLAsync(new Regex("cashu/wallet"), new() { Timeout = 15_000 });
        await s.Page.AssertNoError();

        var content = await s.Page.ContentAsync();
        // Should show either "No spent proofs found" or "Removed X spent proof(s)"
        Assert.True(
            content.Contains("No spent proofs", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Removed", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("No proofs to check", StringComparison.OrdinalIgnoreCase),
            "Expected a status message about spent proof removal");
    }

    // ── Mint info API ───────────────────────────────────────────────────────

    [Fact]
    public async Task MintInfoApiReturnsValidData()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        // GetMintInfo requires auth (inherits [Authorize] from controller) — use Playwright page
        var mintInfoUrl = $"/cashu/mint-info?mintUrl={Uri.EscapeDataString(CdkMintUrl)}";
        var response = await s.Page.APIRequest.GetAsync(
            s.ServerUri.AbsoluteUri.TrimEnd('/') + mintInfoUrl);

        Assert.Equal(200, response.Status);
        var json = await response.TextAsync();
        helper.WriteLine($"Mint info: {json}");

        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Should have url field matching what we queried
        Assert.Equal(CdkMintUrl, root.GetProperty("url").GetString());

        // Should have currency array with "sat"
        var currencies = root.GetProperty("currency");
        Assert.True(currencies.GetArrayLength() > 0, "Expected at least one currency");

        // Should have nuts (supported NUTs)
        var nuts = root.GetProperty("nuts");
        Assert.True(nuts.GetArrayLength() > 0, "Expected at least one supported NUT");
    }
}
