using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Tests;
using NBitcoin;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class GreenfieldApiTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private string CdkMintUrl => PlaywrightTesterCashuUtils.GetCdkMintUrl();

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<(PlaywrightTester s, string storeId, HttpClient http)> SetupAsync(
        params string[] policies
    )
    {
        var s = CreatePlaywrightTester();
        await s.StartAsync();
        await s.RegisterNewUser(isAdmin: true);
        await s.SkipWizard();
        var (_, storeId) = await s.CreateNewStore();

        var btcpayClient = await s.AsTestAccount().CreateClient(policies);

        var http = new HttpClient { BaseAddress = s.ServerUri };
        http.DefaultRequestHeaders.Add("Authorization", $"token {btcpayClient.APIKey}");

        return (s, storeId, http);
    }


    private async Task CreateWalletAsync(HttpClient http, string storeId)
    {
        var r = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private async Task EnableCashuAsync(HttpClient http, string storeId, string? mintUrl = null)
    {
        mintUrl ??= CdkMintUrl;
        var r = await http.PutAsync($"/api/v1/stores/{storeId}/cashu", Json(new
        {
            enabled = true,
            paymentModel = "TrustedMintsOnly",
            trustedMintsUrls = new[] { mintUrl }
        }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private async Task<string> CreateInvoiceAsync(HttpClient http, string storeId, decimal amount = 100, string currency = "SATS")
    {
        var r = await http.PostAsync($"/api/v1/stores/{storeId}/invoices", Json(new { amount, currency }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await ReadJson(r)).GetProperty("id").GetString();
        Assert.NotNull(id);
        return id!;
    }

    private async Task<ulong> FundWalletAsync(
        PlaywrightTester s,
        string storeId,
        HttpClient http,
        string mnemonic,
        ulong amountSat = 200
    )
    {
        var restoreResp = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/restore", Json(new
        {
            mnemonic,
            mintUrls = new[] { CdkMintUrl }
        }));
        Assert.Equal(HttpStatusCode.OK, restoreResp.StatusCode);

        await EnableCashuAsync(http, storeId);

        var invoiceId = await s.CreateInvoice(storeId, amount: 1, currency: "USD");
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(amountSat);
        await s.PayWithTokenViaCheckout(invoiceId, token);

        return amountSat;
    }


    [Fact]
    public async Task GetConfig_ReturnsDefaultConfig()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanViewStoreSettings);
        await using var _ = s;

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.False(json.GetProperty("enabled").GetBoolean());
        helper.WriteLine($"Config: {json}");
    }

    [Fact]
    public async Task UpdateConfig_WithoutWallet_Returns404()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        var r = await http.PutAsync($"/api/v1/stores/{storeId}/cashu", Json(new
        {
            enabled = true,
            paymentModel = "TrustedMintsOnly",
            trustedMintsUrls = new[] { CdkMintUrl }
        }));

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var json = await ReadJson(r);
        Assert.Equal("paymentmethod-not-configured", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdateConfig_AfterWalletCreated_Succeeds()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);

        var r = await http.PutAsync($"/api/v1/stores/{storeId}/cashu", Json(new
        {
            enabled = true,
            paymentModel = "TrustedMintsOnly",
            trustedMintsUrls = new[] { CdkMintUrl }
        }));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await ReadJson(r);
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("TrustedMintsOnly", json.GetProperty("paymentModel").GetString());
    }

    [Fact]
    public async Task GetConfig_WithoutPermission_Returns403()
    {
        var (s, storeId, http) = await SetupAsync(/* no perms */);
        await using var _ = s;

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task CreateWallet_ReturnsNewMnemonic()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        var r = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        var mnemonic = json.GetProperty("mnemonic").GetString();
        helper.WriteLine($"Mnemonic: {mnemonic}");

        Assert.NotNull(mnemonic);
        Assert.Equal(12, mnemonic!.Split(' ').Length);
    }

    [Fact]
    public async Task CreateWallet_Twice_Returns422()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        var r1 = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var r2 = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet", Json(new { }));
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);

        var json = await ReadJson(r2);
        Assert.Equal("wallet-already-exists", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RestoreWallet_WithValidMnemonic_StartsJob()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        var r = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/restore", Json(new
        {
            mnemonic,
            mintUrls = new[] { CdkMintUrl }
        }));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await ReadJson(r);
        Assert.True(json.TryGetProperty("jobId", out var jobId));
        helper.WriteLine($"Restore job: {jobId.GetString()}");
    }

    [Fact]
    public async Task RestoreWallet_InvalidMnemonic_Returns422()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        var r = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/restore", Json(new
        {
            mnemonic = "totally invalid words here not a valid bip39 mnemonic at all ok",
            mintUrls = new[] { CdkMintUrl }
        }));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var json = await ReadJson(r);
        Assert.Equal("invalid-mnemonic", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetRestoreStatus_UnknownJob_Returns404()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanViewStoreSettings);
        await using var _ = s;

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/wallet/restore/nonexistent-job-id");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task GetRestoreStatuses_ReturnsEmptyThenPopulatesAfterRestore()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        // before any restore list is empty
        var r1 = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/wallet/restore");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var before = await ReadJson(r1);
        Assert.Equal(JsonValueKind.Array, before.ValueKind);
        Assert.Equal(0, before.GetArrayLength());

        // start a restore job
        await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/restore", Json(new
        {
            mnemonic,
            mintUrls = new[] { CdkMintUrl }
        }));

        // list now contains the job
        var r2 = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/wallet/restore");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var after = await ReadJson(r2);
        Assert.Equal(JsonValueKind.Array, after.ValueKind);
        Assert.True(after.GetArrayLength() > 0, "Expected at least one restore job in the list");
    }

    [Fact]
    public async Task GetWalletBalances_EmptyWallet_ReturnsEmpty()
    {
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        await CreateWalletAsync(http, storeId);

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/wallet/balances");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal(0, json.GetProperty("balances").GetArrayLength());
    }

    [Fact]
    public async Task DeleteWallet_ThenCreateAgain_Succeeds()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);

        var del = await http.DeleteAsync($"/api/v1/stores/{storeId}/cashu/wallet");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        await CreateWalletAsync(http, storeId);
    }

    [Fact]
    public async Task CheckTokenStates_NoTokens_ReturnsZero()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);

        var r = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/wallet/check-token-states",
            Json(new { })
        );

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await ReadJson(r);
        Assert.Equal(0, json.GetProperty("markedSpent").GetInt32());
    }

    [Fact]
    public async Task RemoveSpentProofs_EmptyWallet_ReturnsZero()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);

        var r = await http.DeleteAsync($"/api/v1/stores/{storeId}/cashu/wallet/spent-proofs");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal(0, json.GetProperty("removed").GetInt32());
    }

    [Fact]
    public async Task ExportBalance_NoProofs_Returns422()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);

        var r = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/export", Json(new
        {
            mintUrl = CdkMintUrl,
            unit = "sat"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var json = await ReadJson(r);
        var code = json.GetProperty("code").GetString();
        // mint may be unreachable in test env (mint-unavailable) or simply no proofs (no-proofs)
        Assert.True(
            code is "no-proofs" or "mint-unavailable",
            $"Unexpected error code: {code}"
        );
    }

    [Fact]
    public async Task GetExportedTokens_EmptyList_ReturnsArray()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanViewStoreSettings);
        await using var _ = s;

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/tokens");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task GetExportedToken_NotFound_Returns404()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanViewStoreSettings);
        await using var _ = s;

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/tokens/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal("token-not-found", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetFailedTransactions_EmptyList_ReturnsArray()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanViewStoreSettings);
        await using var _ = s;

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/failed-transactions");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task RetryFailedTransaction_NotFound_Returns404()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        var r = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/failed-transactions/{Guid.NewGuid()}/retry",
            Json(new { })
        );

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var json = await ReadJson(r);
        Assert.Equal("failed-transaction-not-found", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenerateSecret_WithoutWallet_Returns404()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        var r = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret",
            Json(new { })
        );

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var json = await ReadJson(r);
        Assert.Equal("wallet-not-found", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenerateSecret_Succeeds()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var tester = s;

        await CreateWalletAsync(http, storeId);

        var r = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret",
            Json(new { })
        );

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await ReadJson(r);
        var secret = json.GetProperty("secret").GetString();
        Assert.NotNull(secret);
        Assert.True(Guid.TryParse(secret, out _), $"Secret is not a valid GUID: {secret}");
    }

    [Fact]
    public async Task GenerateSecret_Twice_Returns422()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);
        await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret",
            Json(new { })
        );

        var r2 = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret",
            Json(new { })
        );

        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
        var json = await ReadJson(r2);
        Assert.Equal("secret-already-exists", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RotateSecret_ReplacesExistingSecret()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);

        var gen = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret",
            Json(new { })
        );
        var originalSecret = (await ReadJson(gen)).GetProperty("secret").GetString();

        var rot = await http.SendAsync(new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret"
        ));
        Assert.Equal(HttpStatusCode.OK, rot.StatusCode);

        var newSecret = (await ReadJson(rot)).GetProperty("secret").GetString();
        Assert.NotNull(newSecret);
        Assert.NotEqual(originalSecret, newSecret);
    }

    [Fact]
    public async Task RevokeSecret_ThenGenerateAgain_Succeeds()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanModifyStoreSettings);
        await using var _ = s;

        await CreateWalletAsync(http, storeId);
        await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret",
            Json(new { })
        );

        var del = await http.DeleteAsync($"/api/v1/stores/{storeId}/cashu/lightning-client-secret");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // after revoke, generate should work again
        var gen2 = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/lightning-client-secret",
            Json(new { })
        );
        Assert.Equal(HttpStatusCode.OK, gen2.StatusCode);
    }

    [Fact]
    public async Task GetWalletBalances_AfterPayment_ShowsBalance()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        await FundWalletAsync(s, storeId, http, mnemonic);

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/wallet/balances");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        var balances = json.GetProperty("balances");
        Assert.True(balances.GetArrayLength() > 0, "Expected at least one balance entry after payment");

        var amount = balances[0].GetProperty("amount").GetUInt64();
        helper.WriteLine($"Balance after payment: {amount} sat");
        Assert.True(amount > 0, $"Expected positive balance, got {amount}");
    }

    [Fact]
    public async Task ExportBalance_AfterPayment_ReturnsToken()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        await FundWalletAsync(s, storeId, http, mnemonic);

        var r = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/export", Json(new
        {
            mintUrl = CdkMintUrl,
            unit = "sat"
        }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        var token = json.GetProperty("token").GetString();
        helper.WriteLine($"Exported token: {token?[..Math.Min(60, token?.Length ?? 0)]}...");

        Assert.NotNull(token);
        Assert.StartsWith("cashu", token);

        var amount = json.GetProperty("amount").GetUInt64();
        Assert.True(amount > 0, $"Expected positive exported amount, got {amount}");
    }

    [Fact]
    public async Task GetExportedTokens_AfterExport_ReturnsEntry()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        await FundWalletAsync(s, storeId, http, mnemonic);

        await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/export", Json(new
        {
            mintUrl = CdkMintUrl,
            unit = "sat"
        }));

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/tokens");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(1, json.GetArrayLength());

        var entry = json[0];
        Assert.False(entry.GetProperty("isUsed").GetBoolean());
        Assert.Equal(CdkMintUrl, entry.GetProperty("mint").GetString());
    }

    [Fact]
    public async Task GetExportedToken_ById_AfterExport_ReturnsEntry()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        await FundWalletAsync(s, storeId, http, mnemonic);

        var exportResp = await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/export", Json(new
        {
            mintUrl = CdkMintUrl,
            unit = "sat"
        }));
        var exportJson = await ReadJson(exportResp);
        var tokenId = exportJson.GetProperty("id").GetString();

        var r = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal(tokenId, json.GetProperty("id").GetString());
    }

    [Fact]
    public async Task RemoveSpentProofs_WithAvailableProofs_ContactsMintAndReturnsResult()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        // After FundWallet the proofs are Available in DB (not yet exported).
        // RemoveSpentProofs queries Available proofs, asks the mint for their state,
        // and removes any that are SPENT. Since we just received these proofs they
        // should still be unspent, so removed == 0 — but the endpoint must actually
        // reach the mint (no failedMints).
        await FundWalletAsync(s, storeId, http, mnemonic);

        var r = await http.DeleteAsync($"/api/v1/stores/{storeId}/cashu/wallet/spent-proofs");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        Assert.Equal(0, json.GetProperty("removed").GetInt32());
        Assert.Equal(0, json.GetProperty("failedMints").GetArrayLength());
    }

    [Fact]
    public async Task CheckTokenStates_AfterExport_ReturnsResult()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings
        );
        await using var _ = s;

        await FundWalletAsync(s, storeId, http, mnemonic);

        await http.PostAsync($"/api/v1/stores/{storeId}/cashu/wallet/export", Json(new
        {
            mintUrl = CdkMintUrl,
            unit = "sat"
        }));

        var r = await http.PostAsync(
            $"/api/v1/stores/{storeId}/cashu/wallet/check-token-states",
            Json(new { })
        );
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var json = await ReadJson(r);
        // token was exported but not redeemed yet, markedSpent should be 0
        Assert.Equal(0, json.GetProperty("markedSpent").GetInt32());
        helper.WriteLine($"CheckTokenStates result: {json}");
    }


    [Fact]
    public async Task PayInvoice_InvalidToken_Returns400()
    {
        var s = CreatePlaywrightTester();
        await s.StartAsync();
        await using var _ = s;

        var http = new HttpClient { BaseAddress = s.ServerUri };

        var r = await http.PostAsync("/cashu/pay-invoice",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", "not-a-valid-cashu-token"),
                new KeyValuePair<string, string>("invoiceId", "some-invoice-id")
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var json = await ReadJson(r);
        Assert.NotNull(json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PayInvoice_UnknownInvoiceId_Returns400()
    {
        var s = CreatePlaywrightTester();
        await s.StartAsync();
        await using var _ = s;

        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(100);

        var http = new HttpClient { BaseAddress = s.ServerUri };
        var r = await http.PostAsync("/cashu/pay-invoice",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", token),
                new KeyValuePair<string, string>("invoiceId", "nonexistent-invoice-id")
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var json = await ReadJson(r);
        Assert.Equal("Invalid invoice", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PayInvoice_InsufficientToken_Returns400()
    {
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings,
            Policies.CanCreateInvoice
        );
        await using var _ = s;

        await CreateWalletAsync(http, storeId);
        await EnableCashuAsync(http, storeId);

        // im pretty sure that we have quite some time b4 100usd be less than 1sat, but i'll be happy to fix that
        var invoiceId = await CreateInvoiceAsync(http, storeId, amount: 100, currency: "USD");
        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(1);

        var r = await http.PostAsync("/cashu/pay-invoice",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", token),
                new KeyValuePair<string, string>("invoiceId", invoiceId)
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var json = await ReadJson(r);
        var error = json.GetProperty("error").GetString();
        helper.WriteLine($"Error: {error}");
        Assert.NotNull(error);
        Assert.Contains("sat", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullSetupViaApi_ReceiveCashuPayment_Returns200()
    {
        var (s, storeId, http) = await SetupAsync(
            Policies.CanViewStoreSettings,
            Policies.CanModifyStoreSettings,
            Policies.CanCreateInvoice
        );
        await using var _ = s;

        await CreateWalletAsync(http, storeId);
        await EnableCashuAsync(http, storeId);
        var invoiceId = await CreateInvoiceAsync(http, storeId, amount: 100, currency: "SATS");

        var token = await PlaywrightTesterCashuUtils.MintCashuTokenAsync(200);

        var payResp = await http.PostAsync("/cashu/pay-invoice",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("token", token),
                new KeyValuePair<string, string>("invoiceId", invoiceId!)
            ]));

        Assert.Equal(HttpStatusCode.OK, payResp.StatusCode);
        var payJson = await ReadJson(payResp);
        var redirectUrl = payJson.GetProperty("redirectUrl").GetString();
        helper.WriteLine($"Redirect URL: {redirectUrl}");
        Assert.NotNull(redirectUrl);
        Assert.Contains(invoiceId, redirectUrl);
    }
}
