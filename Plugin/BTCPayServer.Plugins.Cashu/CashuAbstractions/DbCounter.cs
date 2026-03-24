using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using Dapper;
using DotNut;
using DotNut.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Cashu.CashuAbstractions;

public class DbCounter : ICounter
{
    private readonly CashuDbContextFactory _dbContextFactory;
    private readonly string _storeId;
    public DbCounter(CashuDbContextFactory dbContextFactory, string storeId)
    {
        _dbContextFactory = dbContextFactory;
        _storeId = storeId;
    }

    public async Task<uint> GetCounterForId(KeysetId keysetId, CancellationToken ct = default)
    {
        await using var db = _dbContextFactory.CreateContext();
        var entry = await db.StoreKeysetCounters.FirstOrDefaultAsync(
            c => c.StoreId == _storeId && c.KeysetId == keysetId,
            ct
        );

        return entry?.Counter ?? 0;
    }

    public async Task<uint> IncrementCounter(KeysetId keysetId, uint bumpBy = 1, CancellationToken ct = default)
    {
        await using var db = _dbContextFactory.CreateContext();
        var conn = db.Database.GetDbConnection();

        var entityType = db.Model.FindEntityType(typeof(StoreKeysetCounter))
                         ?? throw new ArgumentNullException(nameof(StoreKeysetCounter), "Can't find StoreKeysetCounter table!");
        var schema = entityType.GetSchema();
        var tableName = entityType.GetTableName();

        string sql = $"""
                          INSERT INTO "{schema}"."{tableName}" ("StoreId", "KeysetId", "Counter")
                          VALUES (@storeId, @keysetId, @bumpBy)
                          ON CONFLICT ("StoreId", "KeysetId")
                          DO UPDATE SET "Counter" = "{tableName}"."Counter" + @bumpBy
                          RETURNING "Counter";
                      """;

        var result = await conn.QuerySingleAsync<long>(sql, new
        {
            storeId = _storeId,
            keysetId = keysetId.ToString(),
            bumpBy = (long)bumpBy
        });

        return (uint)result;
    }


    public async Task<(uint oldValue, uint newValue)> FetchAndIncrement(
        KeysetId keysetId,
        uint bumpBy = 1,
        CancellationToken ct = default
    )
    {
        await using var db = _dbContextFactory.CreateContext();
        var conn = db.Database.GetDbConnection();

        var entityType = db.Model.FindEntityType(typeof(StoreKeysetCounter))
                         ?? throw new ArgumentNullException(nameof(StoreKeysetCounter), "Can't find StoreKeysetCounter table!");
        var schema = entityType.GetSchema();
        var tableName = entityType.GetTableName();

        string sql = $"""
                          INSERT INTO "{schema}"."{tableName}" ("StoreId", "KeysetId", "Counter")
                          VALUES (@storeId, @keysetId, @bumpBy)
                          ON CONFLICT ("StoreId", "KeysetId")
                          DO UPDATE SET "Counter" = "{tableName}"."Counter" + @bumpBy
                          RETURNING ("Counter" - @bumpBy) AS "OldValue", "Counter" AS "NewValue";
                      """;

        var result = await conn.QuerySingleAsync<dynamic>(sql, new
        {
            storeId = _storeId,
            keysetId = keysetId.ToString(),
            bumpBy = (long)bumpBy
        });

        return ((uint)(long)result.OldValue, (uint)(long)result.NewValue);
    }


    public async Task SetCounter(KeysetId keysetId, uint counter, CancellationToken ct = default)
    {
        await using var db = _dbContextFactory.CreateContext();
        var conn = db.Database.GetDbConnection();

        var entityType = db.Model.FindEntityType(typeof(StoreKeysetCounter))
                         ?? throw new ArgumentNullException(nameof(StoreKeysetCounter), "Can't find StoreKeysetCounter table!");
        var schema = entityType.GetSchema();
        var tableName = entityType.GetTableName();

        string sql = $"""
                          INSERT INTO "{schema}"."{tableName}" ("StoreId", "KeysetId", "Counter")
                          VALUES (@storeId, @keysetId, @counter)
                          ON CONFLICT ("StoreId", "KeysetId")
                          DO UPDATE SET "Counter" = @counter;
                      """;

        await conn.ExecuteAsync(sql, new
        {
            storeId = _storeId,
            keysetId = keysetId.ToString(),
            counter = (long)counter
        });
    }

    public async Task<IReadOnlyDictionary<KeysetId, uint>> Export()
    {
        await using var db = _dbContextFactory.CreateContext();
        var counters = await db.StoreKeysetCounters.Where(c => c.StoreId == _storeId).ToListAsync();

        return counters.ToDictionary(c => c.KeysetId, c => c.Counter);
    }
}
