using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.Cashu.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CashuDbContext>
{
    public CashuDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<CashuDbContext>();

        // FIXME: Somehow the DateTimeOffset column types get messed up when not using Postgres
        // https://docs.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers?tabs=dotnet-core-cli
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");

        return new CashuDbContext(builder.Options, true);
    }
}
