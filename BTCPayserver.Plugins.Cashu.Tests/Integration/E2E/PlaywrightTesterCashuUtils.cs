using System.Text.RegularExpressions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Tests;
using DotNut;
using DotNut.Abstractions;
using Microsoft.Playwright;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

public static class PlaywrightTesterCashuUtils
{
    public static string GetCdkMintUrl() =>
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    public static string GetNutshellMintUrl() =>
        Environment.GetEnvironmentVariable("TEST_NUTSHELL_MINT_URL") ?? "http://localhost:3339";

    public static string GetCustomerLndUrl() =>
        (
            Environment.GetEnvironmentVariable("TEST_CUSTOMERLND") ?? "http://localhost:35532"
        ).TrimEnd('/');

    public static string GetMerchantLndUrl() =>
        (
            Environment.GetEnvironmentVariable("TEST_MERCHANTLND") ?? "http://localhost:35531/"
        ).TrimEnd('/');

    public static async Task GoToCashuSettings(this PlaywrightTester s, string storeId)
    {
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.AssertNoError();
    }

    public static async Task GoToCashuWallet(this PlaywrightTester s, string storeId)
    {
        await s.GoToUrl($"/stores/{storeId}/cashu/wallet");
    }

    public static async Task GoToCashuStoreConfig(this PlaywrightTester s, string storeId)
    {
        await s.GoToUrl($"/stores/{storeId}/cashu");
        await s.Page.AssertNoError();
    }

    public static async Task SetupFreshCashuWallet(this PlaywrightTester s, string storeId)
    {
        // navigate to getting-started page
        await s.GoToUrl($"/stores/{storeId}/cashu/getting-started");
        await s.Page.AssertNoError();

        // click "Create new wallet"
        await s.Page.ClickAsync("#GenerateWalletLink");
        await s.Page.AssertNoError();

        // should land on create-mnemonic page
        if (!s.Page.Url.Contains("create-mnemonic"))
        {
            throw new InvalidOperationException("Invalid redirect url!");
        }

        // read mnemonic
        var mnemonic = await s.Page.Locator("#RecoveryPhrase").GetAttributeAsync("data-mnemonic");
        if (mnemonic is null || mnemonic.Split(' ').Length != 12)
        {
            throw new InvalidOperationException("Invalid mnemonic!");
        }

        // confirm
        await s.Page.ClickAsync("#confirm");
        await s.Page.ClickAsync("#submit");

        // should land on confirm-mnemonic page
        await s.Page.WaitForURLAsync(new Regex("confirm-mnemonic"));
        await s.Page.AssertNoError();

        // use the mnemonic we read from the create page — confirm page has scrambled words
        // words can repeat in a mnemonic, so we must click unselected matches only
        var first4Words = mnemonic.Split(' ').Take(4).ToArray();

        foreach (var word in first4Words)
        {
            await s.Page.Locator($"#RecoveryPhrase li[data-word='{word}']:not(.clicked)").First.ClickAsync();
        }

        // submit confirmation — #proceed becomes active after 4 words selected
        await s.Page.WaitForFunctionAsync(
            "document.getElementById('proceed').classList.contains('active')"
        );
        await s.Page.ClickAsync("#proceed");

        // should land on cashu store config
        await s.Page.WaitForURLAsync(new Regex("/cashu"));
        await s.Page.AssertNoError();
    }

    public static async Task RestoreCashuWallet(
        this PlaywrightTester s,
        string storeId,
        string mnemonic,
        string mintUrl
    )
    {
        await s.GoToUrl($"/stores/{storeId}/cashu/getting-started");
        await s.Page.ClickAsync("#ImportWalletOptionsLink");

        await FillRestoreFormAsync(s.Page, mnemonic, mintUrl);
        await s.Page.ClickAsync("#proceed");

        await s.Page.WaitForURLAsync(new Regex("restore-status"), new() { Timeout = 10_000 });
        await WaitForRestoreCompletedAsync(s.Page);
        await s.Page.ClickAsync(".finish-btn");
    }

    // ── Enable / configure ───────────────────────────────────────────────────

    public static async Task EnableCashu(this PlaywrightTester s, string storeId)
    {
        await s.GoToUrl($"/stores/{storeId}/cashu");
        var toggle = s.Page.Locator("input[id='Enabled']");
        if (!await toggle.IsCheckedAsync())
            await toggle.ClickAsync();
        await s.ClickPagePrimary();
        await s.FindAlertMessage(StatusMessageModel.StatusSeverity.Success);
    }

    public static async Task EnableCashuPayments(
        this PlaywrightTester s,
        string storeId,
        string paymentMode = "TrustedMintsOnly",
        string? trustedMintUrl = null
    )
    {
        trustedMintUrl ??= GetCdkMintUrl();

        // Step 1: Enable Cashu toggle
        await s.GoToUrl($"/stores/{storeId}/cashu");
        var toggle = s.Page.Locator("input[id='Enabled']");
        if (!await toggle.IsCheckedAsync())
            await toggle.ClickAsync();
        await s.ClickPagePrimary();
        await s.FindAlertMessage(StatusMessageModel.StatusSeverity.Success);

        // Step 2: Set payment mode and trusted mints
        await s.GoToUrl($"/stores/{storeId}/cashu");

        var modeId = paymentMode switch
        {
            "AutoConvert" => "autoConvert",
            "HoldWhenTrusted" => "holdWhenTrusted",
            _ => "trustedMintsOnly",
        };
        await s.Page.Locator($"label[for='{modeId}']").ClickAsync();

        // Add trusted mint (visible for TrustedMintsOnly and HoldWhenTrusted)
        if (paymentMode != "AutoConvert")
        {
            await s.Page.Locator("#mintUrl").FillAsync(trustedMintUrl);
            await s.Page.Locator("[data-action='add-mint']").ClickAsync();
        }

        await s.ClickPagePrimary();
        await s.FindAlertMessage(StatusMessageModel.StatusSeverity.Success);
    }

    // ── Lightning setup ──────────────────────────────────────────────────────

    public static async Task SetupCashuAsLightningNode(
        this PlaywrightTester s,
        string storeId,
        string mintUrl
    )
    {
        // Generate secret
        await s.GoToUrl($"/stores/{storeId}/cashu/settings");
        await s.Page.AssertNoError();

        var generateBtn = s.Page.Locator("button", new() { HasText = "Generate Secret" });
        if (await generateBtn.IsVisibleAsync())
        {
            await generateBtn.ClickAsync();
            await s.Page.WaitForURLAsync(new Regex("cashu/settings"), new() { Timeout = 10_000 });
        }

        // Set up as Lightning node
        var connString = $"type=cashu;mint-url={mintUrl};store-id={storeId};allowinsecure=true";
        await s.GoToLightningSettings();
        await s.Page.ClickAsync("label[for=\"LightningNodeType-Custom\"]");
        await s.Page.FillAsync("#ConnectionString", connString);
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

    public static async Task SetupLightningNode(this PlaywrightTester s, string? lndUrl = null)
    {
        lndUrl ??= GetMerchantLndUrl();
        var connectionString = $"type=lnd-rest;server={lndUrl};allowinsecure=true";
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

    // ── Checkout / payment ───────────────────────────────────────────────────

    public static async Task PayWithTokenViaCheckout(
        this PlaywrightTester s,
        string invoiceId,
        string token
    )
    {
        await s.GoToInvoiceCheckout(invoiceId);
        await s.Page.AssertNoError();

        var cashuTab = s.Page.Locator(".payment-method", new() { HasText = "Cashu" });
        if (await cashuTab.IsVisibleAsync())
            await cashuTab.ClickAsync();

        await s.Page.WaitForSelectorAsync("input[name='token']", new() { Timeout = 15_000 });
        await s.Page.FillAsync("input[name='token']", token);

        var responseTask = s.Page.WaitForResponseAsync(
            r => r.Url.Contains("cashu/pay-invoice"),
            new() { Timeout = 30_000 }
        );
        await s.Page.Locator("#payButton").ClickAsync();
        var response = await responseTask;

        if ((int)response.Status >= 400)
        {
            var body = await response.TextAsync();
            Xunit.Assert.Fail($"Payment request failed with status {response.Status}: {body}");
        }

        // The controller returns Ok({ redirectUrl }) and the JS does window.location.href = redirectUrl.
        // Since the checkout URL already matches /i/{invoiceId}, we can't rely on WaitForURLAsync.
        // Instead, wait for the settled state which confirms the payment completed and the page reloaded.
        await s.Page.WaitForSelectorAsync("#settled", new() { Timeout = 30_000 });
    }

    // ── Minting tokens ──────────────────────────────────────────────────────

    /// <summary>
    /// Mints Cashu tokens by paying a Lightning invoice through customer LND.
    /// Returns the encoded cashuB token string.
    /// </summary>
    public static async Task<string> MintCashuTokenAsync(
        ulong amountSat,
        string? mintUrl = null,
        string? customerLndUrl = null
    )
    {
        mintUrl ??= GetCdkMintUrl();
        customerLndUrl ??= GetCustomerLndUrl();

        var wallet = Wallet.Create().WithMint(mintUrl);

        var mintHandler = await wallet
            .CreateMintQuote()
            .WithAmount(amountSat)
            .WithUnit("sat")
            .ProcessAsyncBolt11();

        var quote = mintHandler.GetQuote();

        using var http = new HttpClient();
        var payBody = System.Text.Json.JsonSerializer.Serialize(
            new { payment_request = quote.Request }
        );
        var payResp = await http.PostAsync(
            $"{customerLndUrl}/v1/channels/transactions",
            new StringContent(payBody, System.Text.Encoding.UTF8, "application/json")
        );
        payResp.EnsureSuccessStatusCode();

        await Task.Delay(2000);

        var proofs = await mintHandler.Mint();

        var cashuToken = new CashuToken
        {
            Tokens = [new CashuToken.Token(mintUrl, proofs)],
            Unit = "sat",
        };
        return cashuToken.Encode();
    }

    /// <summary>
    /// Mints tokens using a deterministic wallet (mnemonic + counter),
    /// so restore can find them later.
    /// </summary>
    public static async Task<long> MintWithMnemonicAsync(
        string mnemonic,
        ulong amountSat,
        string mintUrl,
        string? customerLndUrl = null
    )
    {
        customerLndUrl ??= GetCustomerLndUrl();

        var counter = new InMemoryCounter();
        var wallet = Wallet.Create().WithMint(mintUrl).WithMnemonic(mnemonic).WithCounter(counter);

        var mintHandler = await wallet
            .CreateMintQuote()
            .WithAmount(amountSat)
            .WithUnit("sat")
            .ProcessAsyncBolt11();

        var quote = mintHandler.GetQuote();

        using var http = new HttpClient();
        var payBody = System.Text.Json.JsonSerializer.Serialize(
            new { payment_request = quote.Request }
        );
        var payResp = await http.PostAsync(
            $"{customerLndUrl}/v1/channels/transactions",
            new StringContent(payBody, System.Text.Encoding.UTF8, "application/json")
        );
        payResp.EnsureSuccessStatusCode();

        await Task.Delay(1000);

        var proofs = await mintHandler.Mint();
        return proofs.Sum(p => (long)p.Amount);
    }

    // ── Restore form helpers ─────────────────────────────────────────────────

    public static async Task FillRestoreFormAsync(IPage page, string mnemonic, string mintUrl)
    {
        var words = mnemonic.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            await page.Locator($"#mnemonic-grid input[data-index='{i}']").FillAsync(words[i]);
        }
        await page.Locator("textarea[name='MintUrls']").FillAsync(mintUrl);
        await page.WaitForFunctionAsync(
            "!document.getElementById('proceed').hasAttribute('disabled')"
        );
    }

    public static async Task WaitForRestoreCompletedAsync(IPage page, int timeoutSec = 60)
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
