using System.Text.RegularExpressions;
using BTCPayServer.Tests;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuOnboardingTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private static readonly string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private string CdkMintUrl =>
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    [Fact]
    public async Task CanCreateCashuWallet()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();

        // navigate to getting-started page
        await s.GoToUrl($"/stores/{storeId}/cashu/getting-started");
        await s.Page.AssertNoError();

        // click "Create new wallet"
        await s.Page.ClickAsync("#GenerateWalletLink");
        await s.Page.AssertNoError();

        // should land on create-mnemonic page
        Assert.Contains("create-mnemonic", s.Page.Url);

        // read mnemonic
        var mnemonic = await s.Page.Locator("#RecoveryPhrase").GetAttributeAsync("data-mnemonic");
        Assert.NotNull(mnemonic);
        Assert.Equal(12, mnemonic!.Split(' ').Length);

        // confirm
        await s.Page.ClickAsync("#confirm");
        await s.Page.ClickAsync("#submit");

        // should land on confirm-mnemonic page
        await s.Page.WaitForURLAsync(new Regex("confirm-mnemonic"));
        await s.Page.AssertNoError();

        // read mnemonic from confirmation page and click first 4 words in order
        var confirmMnemonic = await s.Page.Locator("#RecoveryPhrase").GetAttributeAsync("data-mnemonic");
        var first4Words = confirmMnemonic!.Split(' ').Take(4).ToArray();

        foreach (var word in first4Words)
        {
            await s.Page.Locator($"#RecoveryPhrase li[data-word='{word}']").ClickAsync();
        }

        // submit confirmation — #proceed becomes active after 4 words selected
        await s.Page.WaitForFunctionAsync("document.getElementById('proceed').classList.contains('active')");
        await s.Page.ClickAsync("#proceed");

        // should land on cashu store config
        await s.Page.WaitForURLAsync(new Regex("/cashu"));
        await s.Page.AssertNoError();
    }


    [Fact]
    public async Task CanRestoreCashuWallet()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();

        await s.GoToUrl($"/stores/{storeId}/cashu/getting-started");
        await s.Page.AssertNoError();

        // click "connect existing wallet"
        await s.Page.ClickAsync("#ImportWalletOptionsLink");
        await s.Page.AssertNoError();
        Assert.Contains("restore-wallet", s.Page.Url);

        await FillRestoreFormAsync(s.Page, TestMnemonic, CdkMintUrl);

        await s.Page.ClickAsync("#proceed");

        // restore status page - wait for completion
        await s.Page.WaitForURLAsync(new Regex("restore-status"), new() { Timeout = 10_000 });

        await WaitForRestoreCompletedAsync(s.Page);

        // click finish, should redirect to cashu store config
        await s.Page.ClickAsync(".finish-btn");
        await s.Page.WaitForURLAsync(new Regex($"/stores/{storeId}/cashu$"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();
    }

    [Fact]
    public async Task RestoreWalletShowsErrorForInvalidMnemonic()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();

        await s.GoToUrl($"/stores/{storeId}/cashu/restore-wallet");

        await FillRestoreFormAsync(s.Page,
            "invalid word1 word2 word3 word4 word5 word6 word7 word8 word9 word10 word11",
            CdkMintUrl);

        await s.Page.ClickAsync("#proceed");

        // should stay on restore-wallet with an error alert
        Assert.Contains("restore-wallet", s.Page.Url);
        await s.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Error);
    }

// helpers 
    private static async Task FillRestoreFormAsync(IPage page, string mnemonic, string mintUrl)
    {
        var words = mnemonic.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            await page.Locator($"#mnemonic-grid input[data-index='{i}']").FillAsync(words[i]);
        }
        await page.Locator("textarea[name='MintUrls']").FillAsync(mintUrl);
        await page.WaitForFunctionAsync(
            "!document.getElementById('proceed').hasAttribute('disabled')");
    }

    private static async Task WaitForRestoreCompletedAsync(IPage page, int timeoutSec = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            var finishBtn = page.Locator(".finish-btn");
            if (await finishBtn.IsVisibleAsync())
            {
                return;
            }

            var errorContainer = page.Locator(".error-container");
            if (await errorContainer.IsVisibleAsync())
            {
                throw new Exception("Restore failed: " + await page.ContentAsync());
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Restore did not complete within {timeoutSec}s");
    }
}
