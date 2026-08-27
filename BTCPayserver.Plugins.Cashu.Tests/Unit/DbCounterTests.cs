using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Wallets;
using DotNut;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class DbCounterTests
{
    [Fact]
    public async Task GetCounterForId_ReturnsStoredValue()
    {
        var dbf = TestDbFactory.Create();
        var storeId = "test_store";
        var keysetId = new KeysetId("0000000000000001");

        await using (var seedCtx = dbf.CreateContext())
        {
            seedCtx.StoreKeysetCounters.Add(
                new StoreKeysetCounter
                {
                    StoreId = storeId,
                    KeysetId = keysetId,
                    Counter = 42,
                }
            );
            await seedCtx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var counter = new DbCounter(dbf, storeId);
        var result = await counter.GetCounterForId(keysetId, TestContext.Current.CancellationToken);

        Assert.Equal((uint)42, result);
    }

    [Fact]
    public async Task GetCounterForId_Missing_ReturnsZero()
    {
        var dbf = TestDbFactory.Create();
        var counter = new DbCounter(dbf, "store");
        var result = await counter.GetCounterForId(new KeysetId("0000000000000001"), TestContext.Current.CancellationToken);
        Assert.Equal((uint)0, result);
    }

    [Fact]
    public async Task SetCounter_NewKeyset_StoresValue()
    {
        var dbf = TestDbFactory.Create();
        var keysetId = new KeysetId("0000000000000001");
        var counter = new DbCounter(dbf, "store");

        await counter.SetCounter(keysetId, 7, TestContext.Current.CancellationToken);

        Assert.Equal(
            (uint)7,
            await counter.GetCounterForId(keysetId, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task SetCounter_LowerValue_DoesNotRewind()
    {
        var dbf = TestDbFactory.Create();
        var keysetId = new KeysetId("0000000000000001");
        var counter = new DbCounter(dbf, "store");

        await counter.SetCounter(keysetId, 500, TestContext.Current.CancellationToken);
        // a restore grinding fewer values than the live wallet already used must not rewind it
        await counter.SetCounter(keysetId, 42, TestContext.Current.CancellationToken);

        Assert.Equal(
            (uint)500,
            await counter.GetCounterForId(keysetId, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task SetCounter_HigherValue_MovesForward()
    {
        var dbf = TestDbFactory.Create();
        var keysetId = new KeysetId("0000000000000001");
        var counter = new DbCounter(dbf, "store");

        await counter.SetCounter(keysetId, 42, TestContext.Current.CancellationToken);
        await counter.SetCounter(keysetId, 500, TestContext.Current.CancellationToken);

        Assert.Equal(
            (uint)500,
            await counter.GetCounterForId(keysetId, TestContext.Current.CancellationToken)
        );
    }
}
