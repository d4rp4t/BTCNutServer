using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class CashuWalletTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private static readonly string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private string CdkMintUrl =>
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    [Fact]
    public async Task CashuWalletPageLoads()
    {
        await using var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();

        var (_, storeId) = await s.CreateNewStore();
        await SetupCashuWalletAsync(s, storeId);

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
        await SetupCashuWalletAsync(s, storeId);

        await s.GoToUrl($"/stores/{storeId}/cashu");
        await s.Page.AssertNoError();
    }


    internal async Task SetupCashuWalletAsync(PlaywrightTester s, string storeId)
    {
        await s.GoToUrl($"/stores/{storeId}/cashu/getting-started");
        await s.Page.ClickAsync("#ImportWalletOptionsLink");

        var words = TestMnemonic.Split(' ');
        for (var i = 0; i < words.Length; i++)
            await s.Page.Locator($"#mnemonic-grid input[data-index='{i}']").FillAsync(words[i]);

        await s.Page.Locator("textarea[name='MintUrls']").FillAsync(CdkMintUrl);
        await s.Page.WaitForFunctionAsync("!document.getElementById('proceed').hasAttribute('disabled')");
        await s.Page.ClickAsync("#proceed");

        await s.Page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("restore-status"),
            new() { Timeout = 10_000 });

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
}
