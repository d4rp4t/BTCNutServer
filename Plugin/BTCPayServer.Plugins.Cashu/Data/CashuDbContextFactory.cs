using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Cashu.Data;

public class CashuDbContextFactory(IOptions<DatabaseOptions> options)
    : BaseDbContextFactory<CashuDbContext>(options, CashuDbContext.DefaultPluginSchema)
{
    public override CashuDbContext CreateContext(
        Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<CashuDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new CashuDbContext(builder.Options);
    }
}