using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class TestDbFactory : CashuDbContextFactory
{
    private readonly DbContextOptions<CashuDbContext> _opts;

    public TestDbFactory(DbContextOptions<CashuDbContext> opts)
        : base(Options.Create(new DatabaseOptions())) => _opts = opts;

    public override CashuDbContext CreateContext(
        Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? _ = null)
        => new(_opts);

    public static TestDbFactory Create()
    {
        DbContextOptions<CashuDbContext> opts;
        var pgConnStr = Environment.GetEnvironmentVariable("TESTS_POSTGRES");
        if (pgConnStr != null)
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(pgConnStr)
            {
                Database = $"cashu_test_{Guid.NewGuid():N}"
            };
            opts = new DbContextOptionsBuilder<CashuDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
        }
        else
        {
            opts = new DbContextOptionsBuilder<CashuDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        }

        using var ctx = new CashuDbContext(opts);
        ctx.Database.EnsureCreated();
        return new TestDbFactory(opts);
    }

    public MintListener CreateMintListener() =>
        new(this, NullLogger<MintListener>.Instance);

    public MintManager CreateMintManager() =>
        new(this);
}
