using System.Net;
using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Tests;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration.E2E;

[Trait("Category", "Integration")]
[Trait("Playwright", "Playwright")]
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class FailedTransactionsApiTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
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

    [Fact]
    public async Task GetFailedTransactions_ReturnsSortedTransactionsWithStructuredStatus()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanViewStoreSettings);
        await using var _ = s;

        var factory = s.Server.PayTester.GetService<CashuDbContextFactory>();
        await using (var ctx = factory.CreateContext())
        {
            ctx.FailedTransactions.AddRange(
                new FailedTransaction
                {
                    InvoiceId = "invoice-older",
                    StoreId = storeId,
                    MintUrl = "https://mint.old.test",
                    Unit = "sat",
                    InputAmount = 100,
                    OperationType = OperationType.Melt,
                    OutputData = [],
                    CreatedAt = new DateTimeOffset(2026, 4, 19, 9, 0, 0, TimeSpan.Zero),
                    RetryCount = 1,
                    LastRetried = new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero),
                    Status = FailedTransactionStatus.Pending,
                    ReasonCode = FailedTransactionReasons.MeltPendingAfterRetry,
                    Details = FailedTransactionReasons.Describe(FailedTransactionReasons.MeltPendingAfterRetry),
                },
                new FailedTransaction
                {
                    InvoiceId = "invoice-newer",
                    StoreId = storeId,
                    MintUrl = "https://mint.new.test",
                    Unit = "sat",
                    InputAmount = 250,
                    OperationType = OperationType.Swap,
                    OutputData = [],
                    CreatedAt = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero),
                    RetryCount = 3,
                    LastRetried = new DateTimeOffset(2026, 4, 22, 12, 30, 0, TimeSpan.Zero),
                    Status = FailedTransactionStatus.FailedTerminal,
                    ReasonCode = FailedTransactionReasons.SwapMintConnectionBroken,
                    Details = FailedTransactionReasons.Describe(FailedTransactionReasons.SwapMintConnectionBroken),
                },
                new FailedTransaction
                {
                    InvoiceId = "invoice-dismissed",
                    StoreId = storeId,
                    MintUrl = "https://mint.hidden.test",
                    Unit = "sat",
                    InputAmount = 1,
                    OperationType = OperationType.Melt,
                    OutputData = [],
                    CreatedAt = new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero),
                    RetryCount = 0,
                    LastRetried = new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero),
                    Status = FailedTransactionStatus.Dismissed,
                    ReasonCode = FailedTransactionReasons.DismissedByUser,
                    Details = FailedTransactionReasons.Describe(FailedTransactionReasons.DismissedByUser),
                }
            );
            await ctx.SaveChangesAsync();
        }

        var response = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/failed-transactions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ReadJson(response);
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(2, json.GetArrayLength());

        var first = json[0];
        Assert.Equal("invoice-newer", first.GetProperty("invoiceId").GetString());
        Assert.Equal("Swap", first.GetProperty("operationType").GetString());
        Assert.Equal("FailedTerminal", first.GetProperty("status").GetString());
        Assert.True(first.GetProperty("canRetry").GetBoolean());
        Assert.True(first.GetProperty("canDismiss").GetBoolean());
        Assert.Equal(
            FailedTransactionReasons.SwapMintConnectionBroken,
            first.GetProperty("reasonCode").GetString());
        Assert.Equal(
            FailedTransactionReasons.Describe(FailedTransactionReasons.SwapMintConnectionBroken),
            first.GetProperty("details").GetString());

        var second = json[1];
        Assert.Equal("invoice-older", second.GetProperty("invoiceId").GetString());
        Assert.Equal("Pending", second.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DismissFailedTransaction_HidesTransactionFromDefaultList()
    {
        var (s, storeId, http) = await SetupAsync(Policies.CanViewStoreSettings, Policies.CanModifyStoreSettings);
        await using var _ = s;

        Guid failedTransactionId;
        var factory = s.Server.PayTester.GetService<CashuDbContextFactory>();
        await using (var ctx = factory.CreateContext())
        {
            var failedTransaction = new FailedTransaction
            {
                InvoiceId = "invoice-dismiss-me",
                StoreId = storeId,
                MintUrl = "https://mint.dismiss.test",
                Unit = "sat",
                InputAmount = 100,
                OperationType = OperationType.Melt,
                OutputData = [],
                CreatedAt = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero),
                RetryCount = 0,
                LastRetried = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero),
                Status = FailedTransactionStatus.Pending,
                ReasonCode = FailedTransactionReasons.StillPending,
                Details = FailedTransactionReasons.Describe(FailedTransactionReasons.StillPending),
            };
            ctx.FailedTransactions.Add(failedTransaction);
            await ctx.SaveChangesAsync();
            failedTransactionId = failedTransaction.Id;
        }

        var dismissResponse = await http.DeleteAsync($"/api/v1/stores/{storeId}/cashu/failed-transactions/{failedTransactionId}");
        Assert.Equal(HttpStatusCode.OK, dismissResponse.StatusCode);

        var dismissedJson = await ReadJson(dismissResponse);
        Assert.Equal("Dismissed", dismissedJson.GetProperty("status").GetString());
        Assert.False(dismissedJson.GetProperty("canRetry").GetBoolean());
        Assert.False(dismissedJson.GetProperty("canDismiss").GetBoolean());
        Assert.Equal(
            FailedTransactionReasons.DismissedByUser,
            dismissedJson.GetProperty("reasonCode").GetString());

        var listResponse = await http.GetAsync($"/api/v1/stores/{storeId}/cashu/failed-transactions");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var listJson = await ReadJson(listResponse);
        Assert.Equal(JsonValueKind.Array, listJson.ValueKind);
        Assert.Equal(0, listJson.GetArrayLength());
    }
}
