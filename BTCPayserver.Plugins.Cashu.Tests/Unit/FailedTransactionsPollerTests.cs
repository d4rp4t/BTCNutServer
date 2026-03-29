using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Data.enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

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
        bool resolved = false,
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
            LastRetried = DateTimeOffset.UtcNow,
            Resolved = resolved,
        };

    private BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller CreatePoller(
        TestDbFactory db) =>
        new(db, null!, null!, null!, new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output))
        {
            PollInterval = TimeSpan.FromDays(1), // disable auto-polling in unit tests
        };


    [Fact]
    public async Task AddFailedTx_SavesToDB()
    {
        var db = TestDbFactory.Create();
        var poller = CreatePoller(db);

        var ftx = MakeFailedTx();
        await poller.AddFailedTx(ftx, CancellationToken.None);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions.FirstOrDefaultAsync(f => f.Id == ftx.Id);
        Assert.NotNull(saved);
        Assert.Equal(InvoiceId, saved.InvoiceId);
        Assert.Equal(StoreId, saved.StoreId);
        Assert.Equal(MintUrl, saved.MintUrl);
    }

    [Fact]
    public async Task AddFailedTx_MultipleTxs_AllSaved()
    {
        var db = TestDbFactory.Create();
        var poller = CreatePoller(db);

        var tx1 = MakeFailedTx();
        var tx2 = MakeFailedTx(type: OperationType.Swap);

        await poller.AddFailedTx(tx1, CancellationToken.None);
        await poller.AddFailedTx(tx2, CancellationToken.None);

        await using var ctx = db.CreateContext();
        var count = await ctx.FailedTransactions.CountAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task AddFailedTx_SetsResolvedFalse()
    {
        var db = TestDbFactory.Create();
        var poller = CreatePoller(db);

        var ftx = MakeFailedTx();
        await poller.AddFailedTx(ftx, CancellationToken.None);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions.FirstAsync(f => f.Id == ftx.Id);
        Assert.False(saved.Resolved);
    }


    [Theory]
    [InlineData(OperationType.Melt)]
    [InlineData(OperationType.Swap)]
    public async Task AddFailedTx_OperationType_PersistedCorrectly(OperationType opType)
    {
        var db = TestDbFactory.Create();
        var poller = CreatePoller(db);

        var ftx = MakeFailedTx(type: opType);
        await poller.AddFailedTx(ftx, CancellationToken.None);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions.FirstAsync(f => f.Id == ftx.Id);
        Assert.Equal(opType, saved.OperationType);
    }


    [Fact]
    public async Task AddFailedTx_WithMeltDetails_PersistsMeltDetails()
    {
        var db = TestDbFactory.Create();
        var poller = CreatePoller(db);

        var ftx = MakeFailedTx();
        ftx.MeltDetails = new MeltDetails
        {
            MeltQuoteId = "melt-quote-123",
            Expiry = DateTimeOffset.UtcNow.AddHours(1),
            LightningInvoiceId = "ln-inv-123",
            Status = "PENDING",
        };

        await poller.AddFailedTx(ftx, CancellationToken.None);

        await using var ctx = db.CreateContext();
        var saved = await ctx.FailedTransactions
            .Include(f => f.MeltDetails)
            .FirstAsync(f => f.Id == ftx.Id);

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
            db, null!, null!, null!,
            new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output));

        Assert.Equal(TimeSpan.FromMinutes(2), poller.PollInterval);
    }

    [Fact]
    public void DefaultBatchSize_Is50()
    {
        var db = TestDbFactory.Create();
        var poller = new BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller(
            db, null!, null!, null!,
            new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output));

        Assert.Equal(50, poller.BatchSize);
    }

    [Fact]
    public void DefaultMaxConcurrencyPerMint_Is3()
    {
        var db = TestDbFactory.Create();
        var poller = new BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller(
            db, null!, null!, null!,
            new XunitLogger<BTCPayServer.Plugins.Cashu.Services.FailedTransactionsPoller>(output));

        Assert.Equal(3, poller.MaxConcurrencyPerMint);
    }


    [Fact]
    public async Task ResolvedTransactions_AreFilteredInDB()
    {
        var db = TestDbFactory.Create();

        await using (var ctx = db.CreateContext())
        {
            ctx.FailedTransactions.AddRange(
                MakeFailedTx(resolved: false),
                MakeFailedTx(resolved: true),
                MakeFailedTx(resolved: false)
            );
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = db.CreateContext();
        var unresolved = await readCtx.FailedTransactions
            .Where(ft => !ft.Resolved)
            .ToListAsync();

        Assert.Equal(2, unresolved.Count);
    }
}
