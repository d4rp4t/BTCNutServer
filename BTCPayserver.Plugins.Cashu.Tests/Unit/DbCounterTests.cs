using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data.Models;
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
            seedCtx.StoreKeysetCounters.Add(new StoreKeysetCounter
            {
                StoreId = storeId,
                KeysetId = keysetId,
                Counter = 42
            });
            await seedCtx.SaveChangesAsync();
        }

        var counter = new DbCounter(dbf, storeId);
        var result = await counter.GetCounterForId(keysetId);

        Assert.Equal((uint)42, result);
    }

    [Fact]
    public async Task GetCounterForId_Missing_ReturnsZero()
    {
        var dbf = TestDbFactory.Create();
        var counter = new DbCounter(dbf, "store");
        var result = await counter.GetCounterForId(new KeysetId("0000000000000001"));
        Assert.Equal((uint)0, result);
    }
}
