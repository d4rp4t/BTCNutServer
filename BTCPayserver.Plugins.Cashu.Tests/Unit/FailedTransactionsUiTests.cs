using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Cashu.Controllers;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class FailedTransactionsUiTests
{
    [Fact]
    public async Task FailedTransactions_ReturnsViewModel_SortedByCreatedAt()
    {
        var db = TestDbFactory.Create();
        var store = new StoreData { Id = "store-1", StoreName = "Test Store" };

        await using (var ctx = db.CreateContext())
        {
            ctx.FailedTransactions.AddRange(
                new FailedTransaction
                {
                    InvoiceId = "invoice-older",
                    StoreId = store.Id,
                    MintUrl = "https://mint.test",
                    Unit = "sat",
                    InputAmount = 100,
                    OperationType = OperationType.Melt,
                    OutputData = [],
                    CreatedAt = new DateTimeOffset(2026, 4, 19, 9, 0, 0, TimeSpan.Zero),
                    RetryCount = 1,
                    LastRetried = new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero),
                    Status = FailedTransactionStatus.Pending,
                    ReasonCode = FailedTransactionReasons.StillPending,
                    Details = FailedTransactionReasons.Describe(FailedTransactionReasons.StillPending),
                },
                new FailedTransaction
                {
                    InvoiceId = "invoice-newer",
                    StoreId = store.Id,
                    MintUrl = "https://mint.test",
                    Unit = "sat",
                    InputAmount = 250,
                    OperationType = OperationType.Swap,
                    OutputData = [],
                    CreatedAt = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero),
                    RetryCount = 3,
                    LastRetried = new DateTimeOffset(2026, 4, 22, 12, 30, 0, TimeSpan.Zero),
                    Status = FailedTransactionStatus.NeedsManualReview,
                    ReasonCode = FailedTransactionReasons.SwapMintConnectionBroken,
                    Details = FailedTransactionReasons.Describe(FailedTransactionReasons.SwapMintConnectionBroken),
                },
                new FailedTransaction
                {
                    InvoiceId = "invoice-dismissed",
                    StoreId = store.Id,
                    MintUrl = "https://mint.test",
                    Unit = "sat",
                    InputAmount = 25,
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
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var controller = new UICashuWalletController(
            null!,
            db,
            db.CreateMintManager(),
            null!,
            null!,
            NullLogger<UICashuWalletController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.SetStoreData(store);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.FailedTransactions(store.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<FailedTransactionsViewModel>(view.Model);
        Assert.Equal(2, model.Transactions.Count);

        Assert.Equal("invoice-newer", model.Transactions[0].InvoiceId);
        Assert.Equal("Swap", model.Transactions[0].OperationLabel);
        Assert.Equal("NeedsManualReview", model.Transactions[0].Status);
        Assert.True(model.Transactions[0].CanRetry);
        Assert.True(model.Transactions[0].CanDismiss);
        Assert.Equal("invoice-older", model.Transactions[1].InvoiceId);
        Assert.Equal("Melt", model.Transactions[1].OperationLabel);
        Assert.Equal("Pending", model.Transactions[1].Status);
        Assert.Equal(FailedTransactionReasons.StillPending, model.Transactions[1].ReasonCode);
    }

    [Fact]
    public void FailedTransactionReasons_ExposeStableDescriptions()
    {
        Assert.Equal(
            "Connection with mint broken while swap.",
            FailedTransactionReasons.Describe(FailedTransactionReasons.SwapMintConnectionBroken));

        Assert.True(FailedTransactionReasons.Descriptions.ContainsKey(FailedTransactionReasons.MeltPendingAfterRetry));
        Assert.True(FailedTransactionReasons.Descriptions.ContainsKey(FailedTransactionReasons.MissingMeltDetails));
    }

    [Theory]
    [InlineData(FailedTransactionStatus.Pending, true, true)]
    [InlineData(FailedTransactionStatus.FailedTerminal, true, true)]
    [InlineData(FailedTransactionStatus.NeedsManualReview, true, true)]
    [InlineData(FailedTransactionStatus.Recovered, false, true)]
    [InlineData(FailedTransactionStatus.Dismissed, false, false)]
    public void FailedTransaction_StatusDerivedActions_AreConsistent(
        FailedTransactionStatus status,
        bool canRetry,
        bool canDismiss)
    {
        var tx = new FailedTransaction
        {
            InvoiceId = "invoice",
            StoreId = "store",
            MintUrl = "https://mint.test",
            Unit = "sat",
            InputAmount = 1,
            OperationType = OperationType.Melt,
            OutputData = [],
            RetryCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            LastRetried = DateTimeOffset.UtcNow,
            Status = status,
        };

        Assert.Equal(canRetry, tx.CanRetryManually);
        Assert.Equal(canDismiss, tx.CanDismiss);
    }
}
