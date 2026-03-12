using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests;

public class DbCounterTests
{
    private class TestCashuDbContextFactory : CashuDbContextFactory
    {
        private readonly DbContextOptions<CashuDbContext> _options;

        public TestCashuDbContextFactory(DbContextOptions<CashuDbContext> options) 
            : base(Options.Create(new DatabaseOptions()))
        {
            _options = options;
        }

        public override CashuDbContext CreateContext(
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            return new CashuDbContext(_options);
        }
    }

    private CashuDbContextFactory CreateDb()
    {
        var options = new DbContextOptionsBuilder<CashuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestCashuDbContextFactory(options);
    }

    [Fact]
    public async Task IncrementCounter_NewEntry_CreatesAndIncrements()
    {
        var dbf = CreateDb();
        var storeId = "test_store";
        var keysetId = new KeysetId("0000000000000001");
        var counter = new DbCounter(dbf, storeId);

        // act
        var result = await counter.IncrementCounter(keysetId, 5);

        // assert
        Assert.Equal((uint)5, result);

        await using var ctx = dbf.CreateContext();
        var entry = await ctx.StoreKeysetCounters.FirstOrDefaultAsync(x => x.StoreId == storeId && x.KeysetId == keysetId);
        Assert.NotNull(entry);
        Assert.Equal((uint)5, entry.Counter);
    }

    [Fact]
    public async Task IncrementCounter_ExistingEntry_Increments()
    {
        var dbf = CreateDb();
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

        // act
        var result = await counter.IncrementCounter(keysetId, 2);

        // assert
        Assert.Equal((uint)12, result);

        await using var ctx = dbf.CreateContext();
        var entry = await ctx.StoreKeysetCounters.FirstAsync(x => x.StoreId == storeId);
        Assert.Equal((uint)12, entry.Counter);
    }

    [Fact]
    public async Task GetCounterForId_ReturnsStoredValue()
    {
        var dbf = CreateDb();
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
        var dbf = CreateDb();
        var counter = new DbCounter(dbf, "store");
        var result = await counter.GetCounterForId(new KeysetId("0000000000000001"));
        Assert.Equal((uint)0, result);
    }
}
