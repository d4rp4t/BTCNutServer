using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class TestDbFactory : CashuDbContextFactory
{
    private readonly DbContextOptions<CashuDbContext> _opts;

    public TestDbFactory(DbContextOptions<CashuDbContext> opts)
        : base(Options.Create(new DatabaseOptions())) => _opts = opts;

    public override CashuDbContext CreateContext(
        Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? _ =
            null
    ) => new(_opts);

    public static TestDbFactory Create()
    {
        DbContextOptions<CashuDbContext> opts;
        var pgConnStr = Environment.GetEnvironmentVariable("TESTS_POSTGRES");
        if (pgConnStr != null)
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(pgConnStr)
            {
                Database = $"cashu_test_{Guid.NewGuid():N}",
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

    public async Task SaveAsync<T>(T entity) where T : class
    {
        await using var ctx = CreateContext();
        ctx.Set<T>().Add(entity);
        await ctx.SaveChangesAsync();
    }

    public MintListener CreateMintListener(ITestOutputHelper? output = null) =>
        new(this, output != null
            ? new XunitLogger<MintListener>(output)
            : NullLogger<MintListener>.Instance)
        { CleanupInterval = TimeSpan.FromSeconds(3) };

    public MintManager CreateMintManager() => new(this);
}

public class XunitLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            if (exception != null)
                output.WriteLine(exception.ToString());
        }
        catch { /* xUnit output can fail if test already finished */ }
    }
}
