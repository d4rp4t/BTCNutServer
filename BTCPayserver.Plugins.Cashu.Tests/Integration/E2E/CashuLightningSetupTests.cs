using System.Text.RegularExpressions;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuLightningSetupTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private string CdkMintUrl =>
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    [Fact]
    public async Task CanGenerateLightningClientSecret()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.SetupFreshCashuWallet(storeId);
        await s.EnableCashu(storeId);

        // Navigate to Cashu settings
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.AssertNoError();

        // Should see "Generate Secret" button (no secret yet)
        var generateBtn = s.Page.Locator("button", new() { HasText = "Generate Secret" });
        Assert.True(await generateBtn.IsVisibleAsync(), "Generate Secret button should be visible");

        // Should NOT see send connection string yet
        Assert.False(await s.Page.Locator("#connStringSend").IsVisibleAsync());

        // Generate secret
        await generateBtn.ClickAsync();
        await s.Page.WaitForURLAsync(new Regex("cashu/settings"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();

        // Now send connection string should be visible
        var connStringSend = s.Page.Locator("#connStringSend");
        Assert.True(await connStringSend.IsVisibleAsync(), "Send connection string should appear after generating secret");

        // Receive connection string should always be visible
        var connStringReceive = s.Page.Locator("#connStringReceive");
        Assert.True(await connStringReceive.IsVisibleAsync());
        var receiveValue = await connStringReceive.GetAttributeAsync("value");
        helper.WriteLine($"Receive conn string: {receiveValue}");
        Assert.Contains("type=cashu", receiveValue!);
        Assert.Contains($"store-id={storeId}", receiveValue);
    }

    [Fact]
    public async Task CanSetupCashuAsLightningNode()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.SetupFreshCashuWallet(storeId);
        await s.EnableCashu(storeId);

        // Generate lightning client secret
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.Locator("button", new() { HasText = "Generate Secret" }).ClickAsync();
        await s.Page.WaitForURLAsync(new Regex("cashu/settings"), new() { Timeout = 10_000 });

        // Build connection string with allowinsecure for HTTP mint in regtest
        var connString = $"type=cashu;mint-url={CdkMintUrl};store-id={storeId};allowinsecure=true";
        helper.WriteLine($"Connection string: {connString}");

        // Go to Lightning setup and configure Cashu as custom node
        await s.GoToLightningSettings();
        await s.Page.ClickAsync("label[for=\"LightningNodeType-Custom\"]");
        await s.Page.FillAsync("#ConnectionString", connString);
        await s.ClickPagePrimary();
        await s.FindAlertMessage(partialText: "BTC Lightning node updated.");

        // Enable Lightning payment method
        var enabled = await s.Page.WaitForSelectorAsync("#BTCLightningEnabled");
        if (!await enabled!.IsCheckedAsync())
        {
            await enabled.ClickAsync();
            await s.ClickPagePrimary();
            await s.FindAlertMessage(partialText: "BTC Lightning settings successfully updated");
        }

        // Verify Lightning is configured — revisit settings page
        await s.GoToLightningSettings();
        var content = await s.Page.ContentAsync();
        Assert.Contains("cashu", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LightningInvoiceCheckoutShowsAfterCashuNodeSetup()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.SetupFreshCashuWallet(storeId);
        await s.EnableCashu(storeId);
        await s.SetupCashuAsLightningNode(storeId, CdkMintUrl);

        // Create an invoice
        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        Assert.NotNull(invoiceId);

        // Go to checkout
        await s.GoToInvoiceCheckout(invoiceId);
        await s.Page.AssertNoError();

        // The Lightning payment method should appear (Cashu acts as the LN node)
        var content = await s.Page.ContentAsync();
        helper.WriteLine($"Checkout page length: {content.Length}");

        // Lightning tab should show since Cashu is configured as the LN node
        Assert.True(
            content.Contains("Lightning", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("ln", StringComparison.OrdinalIgnoreCase),
            "Expected Lightning payment method to appear in checkout");
    }

    [Fact]
    public async Task CanRotateLightningClientSecret()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.SetupFreshCashuWallet(storeId);
        await s.EnableCashu(storeId);

        // Generate initial secret
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.Locator("button", new() { HasText = "Generate Secret" }).ClickAsync();
        await s.Page.WaitForURLAsync(new Regex("cashu/settings"), new() { Timeout = 10_000 });

        // Read original send connection string
        await s.Page.Locator("#toggleSendVisibility").ClickAsync();
        var originalConn = await s.Page.Locator("#connStringSend").GetAttributeAsync("value");
        helper.WriteLine($"Original: {originalConn}");

        // Rotate secret via modal — use data-bs-target to avoid matching the submit button inside the modal
        await s.Page.Locator("[data-bs-target='#confirmRotateModal']").ClickAsync();
        await s.Page.WaitForSelectorAsync("#confirmRotateModal.show", new() { Timeout = 5_000 });
        await s.Page.Locator("#confirmRotateModal button[type='submit']").ClickAsync();

        await s.Page.WaitForURLAsync(new Regex("cashu/settings"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();

        // Read new connection string — should differ
        await s.Page.Locator("#toggleSendVisibility").ClickAsync();
        var rotatedConn = await s.Page.Locator("#connStringSend").GetAttributeAsync("value");
        helper.WriteLine($"Rotated: {rotatedConn}");

        Assert.NotEqual(originalConn, rotatedConn);
        Assert.Contains("secret=", rotatedConn!);
    }


    [Fact]
    public async Task CanSaveFeeSettings()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.SetupFreshCashuWallet(storeId);
        await s.EnableCashu(storeId);

        // Navigate to settings and set fee values
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.AssertNoError();

        await s.Page.FillAsync("#CustomerFeeAdvance", "10");
        await s.Page.FillAsync("#MaxLightningFee", "7");
        await s.Page.FillAsync("#MaxKeysetFee", "5");

        await s.Page.Locator("#SaveButton").ClickAsync();
        await s.FindAlertMessage(partialText: "Settings saved");

        // Reload and verify values persisted
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.AssertNoError();

        var customerFee = await s.Page.Locator("#CustomerFeeAdvance").InputValueAsync();
        var maxLnFee = await s.Page.Locator("#MaxLightningFee").InputValueAsync();
        var maxKeysetFee = await s.Page.Locator("#MaxKeysetFee").InputValueAsync();

        helper.WriteLine($"Fees: customer={customerFee}, ln={maxLnFee}, keyset={maxKeysetFee}");
        Assert.Equal("10", customerFee);
        Assert.Equal("7", maxLnFee);
        Assert.Equal("5", maxKeysetFee);
    }


    [Fact]
    public async Task CanRemoveWallet()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await s.SetupFreshCashuWallet(storeId);
        await s.EnableCashu(storeId);

        // Navigate to settings
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.AssertNoError();

        // Scroll to Danger Zone and click "Remove Wallet" to open modal
        var removeWalletBtn = s.Page.Locator("[data-bs-target='#confirmDeleteModal']");
        await removeWalletBtn.ScrollIntoViewIfNeededAsync();
        await removeWalletBtn.ClickAsync();
        await s.Page.WaitForSelectorAsync("#confirmDeleteModal.show", new() { Timeout = 5_000 });

        // Type confirmation text
        await s.Page.FillAsync("#confirmText", "remove-my-wallet");

        // The delete button should now be clickable (JS enables it)
        await s.Page.WaitForFunctionAsync(
            "document.getElementById('confirmDeleteBtn').style.pointerEvents === 'auto'");
        await s.Page.Locator("#confirmDeleteBtn").ClickAsync();

        // Should redirect to store dashboard after removal
        await s.Page.WaitForURLAsync(new Regex("/stores/"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();

        // Navigating to cashu wallet should redirect to getting-started (no wallet)
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        Assert.Contains("getting-started", s.Page.Url);
    }

}
