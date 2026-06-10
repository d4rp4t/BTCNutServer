using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

/// <summary>
/// Unit tests for FailedTransactionsPoller that don't require StoreRepository or InvoiceRepository.
/// Tests focus on DB persistence, tracking, and batch/concurrency config.
/// </summary>
public class FailedTransactionsPollerTests(ITestOutputHelper output)
{
    private const string StoreId = "test-store";
    private const string MintUrl = "https://fake-mint.test";
    private const string InvoiceId = "btcpay-invoice-123";

    private static FailedTransaction MakeFailedTx(
        string mintUrl = MintUrl,
        FailedTransactionStatus status = FailedTransactionStatus.Pending,
        OperationType type = OperationType.Melt) => new()
        {
            InvoiceId = InvoiceId,
            StoreId = StoreId,
            MintUrl = mintUrl,
            Unit = "sat",
            InputAmount = 100,
            OperationType = type,
            OutputData = [],
            RetryCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            LastRetried = DateTimeOffset.UtcNow,
            Status = status,
            ReasonCode = FailedTransactionReasons.StillPending,
        };

    private BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller CreatePoller(
        TestDbFactory db) =>
        new(db, null!, null!, new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output), null!, null!)
        {
            PollInterval = TimeSpan.FromDays(1), // disable auto-polling in unit tests
        };


    [Fact]
    public async Task FailedTx_SavesToDB()
    {
        var db = TestDbFactory.Create();
        var ftx = MakeFailedTx();
        await db.SaveAsync(ftx);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions.FirstOrDefaultAsync(f => f.Id == ftx.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal(InvoiceId, saved.InvoiceId);
        Assert.Equal(StoreId, saved.StoreId);
        Assert.Equal(MintUrl, saved.MintUrl);
    }

    [Fact]
    public async Task FailedTx_MultipleTxs_AllSaved()
    {
        var db = TestDbFactory.Create();
        await db.SaveAsync(MakeFailedTx());
        await db.SaveAsync(MakeFailedTx(type: OperationType.Swap));

        await using var ctx = db.CreateContext();
        Assert.Equal(2, await ctx.FailedTransactions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedTx_DefaultsToPendingStatus()
    {
        var db = TestDbFactory.Create();
        var ftx = MakeFailedTx();
        await db.SaveAsync(ftx);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions.FirstAsync(f => f.Id == ftx.Id, TestContext.Current.CancellationToken);
        Assert.Equal(FailedTransactionStatus.Pending, saved.Status);
    }

    [Theory]
    [InlineData(OperationType.Melt)]
    [InlineData(OperationType.Swap)]
    public async Task FailedTx_OperationType_PersistedCorrectly(OperationType opType)
    {
        var db = TestDbFactory.Create();
        var ftx = MakeFailedTx(type: opType);
        await db.SaveAsync(ftx);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions.FirstAsync(f => f.Id == ftx.Id, TestContext.Current.CancellationToken);
        Assert.Equal(opType, saved.OperationType);
    }

    [Fact]
    public async Task FailedTx_WithMeltDetails_PersistsMeltDetails()
    {
        var db = TestDbFactory.Create();
        var ftx = MakeFailedTx();
        ftx.MeltDetails = new MeltDetails
        {
            MeltQuoteId = "melt-quote-123",
            Expiry = DateTimeOffset.UtcNow.AddHours(1),
            LightningInvoiceId = "ln-inv-123",
            Status = "PENDING",
        };
        await db.SaveAsync(ftx);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions
            .Include(f => f.MeltDetails)
            .FirstAsync(f => f.Id == ftx.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(saved.MeltDetails);
        Assert.Equal("melt-quote-123", saved.MeltDetails.MeltQuoteId);
        Assert.Equal("PENDING", saved.MeltDetails.Status);
    }

    [Fact]
    public void DefaultPollInterval_IsTwoMinutes()
    {
        var db = TestDbFactory.Create();
        // Use real constructor defaults (not CreatePoller which overrides PollInterval)
        var poller = new BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller(
            db, null!, null!,
            new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output), null!, null!);

        Assert.Equal(TimeSpan.FromMinutes(2), poller.PollInterval);
    }

    [Fact]
    public void DefaultBatchSize_Is50()
    {
        var db = TestDbFactory.Create();
        var poller = new BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller(
            db, null!, null!,
            new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output), null!, null!);

        Assert.Equal(50, poller.BatchSize);
    }

    [Fact]
    public void DefaultMaxConcurrencyPerMint_Is3()
    {
        var db = TestDbFactory.Create();
        var poller = new BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller(
            db, null!, null!,
            new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output), null!, null!);

        Assert.Equal(3, poller.MaxConcurrencyPerMint);
    }


    [Fact]
    public async Task PendingTransactions_AreFilteredInDB()
    {
        var db = TestDbFactory.Create();

        await using (var ctx = db.CreateContext())
        {
            ctx.FailedTransactions.AddRange(
                MakeFailedTx(status: FailedTransactionStatus.Pending),
                MakeFailedTx(status: FailedTransactionStatus.Recovered),
                MakeFailedTx(status: FailedTransactionStatus.Pending)
            );
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readCtx = db.CreateContext();
        var unresolved = await readCtx.FailedTransactions
            .Where(ft => ft.Status == FailedTransactionStatus.Pending)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, unresolved.Count);
    }
}
