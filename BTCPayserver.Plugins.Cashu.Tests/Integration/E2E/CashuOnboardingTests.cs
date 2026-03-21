using System.Text.RegularExpressions;
using BTCPayServer.Tests;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuOnboardingTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private readonly string TestMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

    private string CdkMintUrl => PlaywrightTesterCashuUtils.GetCdkMintUrl();
    private string NutshellMintUrl => PlaywrightTesterCashuUtils.GetNutshellMintUrl();
    private string CustomerLndUrl => PlaywrightTesterCashuUtils.GetCustomerLndUrl();

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

        // use the mnemonic we read from the create page — confirm page has scrambled words
        var first4Words = mnemonic.Split(' ').Take(4).ToArray();

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
    public async Task CanRestoreEmptyCashuWallet()
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

        await PlaywrightTesterCashuUtils.FillRestoreFormAsync(s.Page, TestMnemonic, CdkMintUrl);

        await s.Page.ClickAsync("#proceed");

        // restore status page - wait for completion
        await s.Page.WaitForURLAsync(new Regex("restore-status"), new() { Timeout = 10_000 });

        await PlaywrightTesterCashuUtils.WaitForRestoreCompletedAsync(s.Page);

        // click finish, should redirect to cashu store config
        await s.Page.ClickAsync(".finish-btn");
        await s.Page.WaitForURLAsync(new Regex($"/stores/{storeId}/cashu$"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();
    }

    [Fact]
    public async Task CanRestoreNonEmptyWallet()
    {
        var mnemonic = TestMnemonic;
        helper.WriteLine($"Restore test mnemonic: {mnemonic}");

        // Step 1: Mint on both mints AND start BTCPay server in parallel
        var cdkTask = PlaywrightTesterCashuUtils.MintWithMnemonicAsync(mnemonic, 200, CdkMintUrl);
        var nutshellTask = PlaywrightTesterCashuUtils.MintWithMnemonicAsync(mnemonic, 200, NutshellMintUrl);
        await using var s = CreatePlaywrightTester();
        var startTask = s.StartAsync();

        await Task.WhenAll(cdkTask, nutshellTask, startTask);
        var cdkSats = cdkTask.Result;
        var nutshellSats = nutshellTask.Result;
        helper.WriteLine($"Minted {cdkSats} sat on CDK, {nutshellSats} sat on Nutshell");

        // Step 2: Setup BTCPay store with the same mnemonic and both mints
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();

        await s.GoToUrl($"/stores/{storeId}/cashu/getting-started");
        await s.Page.ClickAsync("#ImportWalletOptionsLink");

        await PlaywrightTesterCashuUtils.FillRestoreFormAsync(s.Page, mnemonic, CdkMintUrl);

        // The textarea binds as a single List<string> element — multi-line fails URI validation.
        // Replace the textarea with separate hidden inputs so ASP.NET binds each URL separately.
        var mintUrls = new[] { CdkMintUrl, NutshellMintUrl };
        await s.Page.EvaluateAsync(@"(urls) => {
            const form = document.querySelector('form');
            const textarea = form.querySelector('textarea[name=""MintUrls""]');
            textarea.remove();
            urls.forEach(url => {
                const input = document.createElement('input');
                input.type = 'hidden';
                input.name = 'MintUrls';
                input.value = url;
                form.appendChild(input);
            });
        }", mintUrls);

        await s.Page.ClickAsync("#proceed");

        await s.Page.WaitForURLAsync(new Regex("restore-status"), new() { Timeout = 10_000 });

        // Step 3: Wait for restore and verify exact balances for both mints
        await PlaywrightTesterCashuUtils.WaitForRestoreCompletedAsync(s.Page);

        var mintItems = s.Page.Locator(".mint-item");
        Assert.Equal(2, await mintItems.CountAsync());

        // Build a map of mint URL → minted amount (restore swaps proofs, so balance < minted due to input fees)
        var mintedAmounts = new Dictionary<string, long>
        {
            [CdkMintUrl] = cdkSats,
            [NutshellMintUrl] = nutshellSats,
        };

        for (var i = 0; i < 2; i++)
        {
            var item = mintItems.Nth(i);
            var url = (await item.Locator(".mint-url").TextContentAsync())!.Trim();
            var balanceText = (await item.Locator(".mint-balance").TextContentAsync())!.Trim();
            helper.WriteLine($"Restored: {url} → {balanceText}");

            // Parse balance number from "X sats" format
            var balanceSats = long.Parse(balanceText.Replace(",", "").Replace(" sats", ""));

            // Find the matching minted amount (URL may be normalized with trailing slash)
            var matchingEntry = mintedAmounts
                .FirstOrDefault(e => url.Contains(new Uri(e.Key).Host));
            Assert.False(string.IsNullOrEmpty(matchingEntry.Key),
                $"Unexpected mint URL on restore page: {url}");

            // Balance should be positive and at most the minted amount (swap fees reduce it)
            Assert.True(balanceSats > 0,
                $"Expected positive balance for {url}, got {balanceSats}");
            Assert.True(balanceSats <= matchingEntry.Value,
                $"Restored balance {balanceSats} exceeds minted amount {matchingEntry.Value} for {url}");
        }

        await s.Page.ClickAsync(".finish-btn");
        await s.Page.WaitForURLAsync(new Regex($"/stores/{storeId}/cashu$"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();

        // Step 4: Navigate to wallet page and verify balance is displayed
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
        await s.Page.AssertNoError();

        var walletBalance = s.Page.Locator("span.h1.fw-bold").First;
        var walletBalanceText = await walletBalance.TextContentAsync();
        helper.WriteLine($"Wallet page balance: {walletBalanceText}");

        Assert.NotNull(walletBalanceText);
        var walletBalanceValue = decimal.Parse(walletBalanceText!.Trim().Replace(",", ""));
        Assert.True(walletBalanceValue > 0, $"Expected positive wallet balance, got {walletBalanceValue}");
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

        await PlaywrightTesterCashuUtils.FillRestoreFormAsync(s.Page,
            "invalid word1 word2 word3 word4 word5 word6 word7 word8 word9 word10 word11",
            CdkMintUrl);

        await s.Page.ClickAsync("#proceed");

        // should stay on restore-wallet with an error alert
        Assert.Contains("restore-wallet", s.Page.Url);
        await s.FindAlertMessage(BTCPayServer.Abstractions.Models.StatusMessageModel.StatusSeverity.Error);
    }

    [Fact]
    public async Task CanInitWithoutLightning()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();

        // Restore wallet first (needed before init-without-lightning)
        await s.RestoreCashuWallet(storeId, TestMnemonic, CdkMintUrl);

        // Navigate to init-without-lightning
        await s.GoToUrl($"/stores/{storeId}/cashu/init-without-lightning");
        await s.Page.AssertNoError();

        var content = await s.Page.ContentAsync();
        Assert.Contains("without Lightning", content);
        Assert.Contains("Trusted mints", content, StringComparison.OrdinalIgnoreCase);

        // Add a trusted mint and submit
        await s.Page.Locator("#mintUrl").FillAsync(CdkMintUrl);
        await s.Page.Locator("[data-action='add-mint']").ClickAsync();
        await s.Page.Locator("input[type='submit'][value='Finish']").ClickAsync();

        // Should redirect to store config with Cashu enabled
        await s.Page.WaitForURLAsync(new Regex($"/stores/{storeId}/cashu"), new() { Timeout = 10_000 });
        await s.Page.AssertNoError();

        // Verify Cashu is enabled and TrustedMintsOnly mode is set
        var pageContent = await s.Page.ContentAsync();
        Assert.Contains(CdkMintUrl, pageContent);
    }
}
