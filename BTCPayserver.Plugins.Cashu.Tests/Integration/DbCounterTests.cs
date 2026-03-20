using BTCPayserver.Plugins.Cashu.Tests.Unit;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration;

[Trait("Category", "Integration")]
public class DbCounterTests
{
    [Fact]
    public async Task IncrementCounter_NewEntry_CreatesAndIncrements()
    {
        var dbf = TestDbFactory.Create();
        var storeId = "test_store";
        var keysetId = new KeysetId("0000000000000001");
        var counter = new DbCounter(dbf, storeId);

        var result = await counter.IncrementCounter(keysetId, 5);

        Assert.Equal((uint)5, result);

        await using var ctx = dbf.CreateContext();
        var entry = await ctx.StoreKeysetCounters.FirstOrDefaultAsync(
            x => x.StoreId == storeId && x.KeysetId == keysetId);
        Assert.NotNull(entry);
        Assert.Equal((uint)5, entry.Counter);
    }

    [Fact]
    public async Task IncrementCounter_ExistingEntry_Increments()
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
                Counter = 10
            });
            await seedCtx.SaveChangesAsync();
        }

        var counter = new DbCounter(dbf, storeId);

        var result = await counter.IncrementCounter(keysetId, 2);

        Assert.Equal((uint)12, result);

        await using var ctx = dbf.CreateContext();
        var entry = await ctx.StoreKeysetCounters.FirstAsync(x => x.StoreId == storeId);
        Assert.Equal((uint)12, entry.Counter);
    }
}
