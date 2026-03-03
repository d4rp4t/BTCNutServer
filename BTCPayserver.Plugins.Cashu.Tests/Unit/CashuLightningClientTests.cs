#nullable enable
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Lightning;
using DotNut;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using Xunit;
using Mnemonic = DotNut.NBitcoin.BIP39.Mnemonic;

namespace BTCPayserver.Plugins.Cashu.Tests;

public class CashuLightningClientTests
{

    private class TestDbFactory : CashuDbContextFactory
    {
        private readonly DbContextOptions<CashuDbContext> _opts;

        public TestDbFactory(DbContextOptions<CashuDbContext> opts)
            : base(Options.Create(new DatabaseOptions())) => _opts = opts;

        public override CashuDbContext CreateContext(
            Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? _ = null)
            => new(_opts);
    }

    private static TestDbFactory CreateDb() =>
        new(new DbContextOptionsBuilder<CashuDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MintListener CreateMintListener(CashuDbContextFactory db) =>
        new(db, NullLogger<MintListener>.Instance);

    private static readonly Uri FakeMint = new("https://fake-mint.test");
    private const string StoreId = "test-store";
    private static readonly Network TestNetwork = Network.RegTest;
    private static readonly Mnemonic TestMnemonic =
        new("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

    private static CashuLightningClient CreateClient(
        CashuDbContextFactory db,
        MintListener listener,
        string? secret = null) =>
        new(FakeMint, StoreId, secret, db, listener, TestNetwork);

    private static async Task SeedWalletConfig(CashuDbContextFactory db, Guid? secret = null)
    {
        await using var ctx = db.CreateContext();
        ctx.CashuWalletConfig.Add(new CashuWalletConfig
        {
            StoreId = StoreId,
            WalletMnemonic = TestMnemonic,
            Verified = true,
            LightningClientSecret = secret,
        });
        await ctx.SaveChangesAsync();
    }

    private static CashuLightningClientInvoice MakeInvoice(string invoiceId, string quoteState = "UNPAID") =>
        new()
        {
            StoreId = StoreId,
            Mint = FakeMint.ToString().TrimEnd('/'),
            QuoteId = Guid.NewGuid().ToString(),
            InvoiceId = invoiceId,
            KeysetId = new KeysetId("0000000000000001"),
            OutputData = [],
            QuoteState = quoteState,
            Amount = LightMoney.Satoshis(100),
            Bolt11 = "lnbc...",
            Created = DateTimeOffset.UtcNow,
            Expiry = DateTimeOffset.UtcNow.AddHours(1),
        };

    private static CashuLightningClientPayment MakePayment(string hash, string quoteState = "PAID") =>
        new()
        {
            StoreId = StoreId,
            Mint = FakeMint.ToString().TrimEnd('/'),
            QuoteId = Guid.NewGuid().ToString(),
            QuoteState = quoteState,
            PaymentHash = hash,
            Bolt11 = "lnbc...",
            Amount = LightMoney.Satoshis(50),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    
    [Fact]
    public async Task Pay_NullSecret_ThrowsInvalidOperation()
    {
        var db = CreateDb();
        await SeedWalletConfig(db, secret: Guid.NewGuid());
        var client = CreateClient(db, CreateMintListener(db), secret: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Pay("lnbc...", CancellationToken.None));
    }

    [Fact]
    public async Task Pay_WrongSecret_ThrowsInvalidOperation()
    {
        var db = CreateDb();
        await SeedWalletConfig(db, secret: Guid.NewGuid());
        var client = CreateClient(db, CreateMintListener(db), secret: Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Pay("lnbc...", CancellationToken.None));
    }

    [Fact]
    public async Task Pay_NoWalletConfig_ThrowsInvalidOperation()
    {
        var db = CreateDb(); 
        var client = CreateClient(db, CreateMintListener(db), secret: Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Pay("lnbc...", CancellationToken.None));
    }

    [Fact]
    public async Task Pay_NoSecret_InConfig_ThrowsInvalidOperation()
    {
        var db = CreateDb();
        await SeedWalletConfig(db, secret: null); 
        var client = CreateClient(db, CreateMintListener(db), secret: Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Pay("lnbc...", CancellationToken.None));
    }


    [Fact]
    public async Task GetInvoice_ExistingId_ReturnsInvoice()
    {
        var db = CreateDb();
        var invoiceId = "abc123";
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(MakeInvoice(invoiceId));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.GetInvoice(invoiceId);

        Assert.NotNull(result);
        Assert.Equal(invoiceId, result.Id);
    }

    [Fact]
    public async Task GetInvoice_MissingId_ReturnsNull()
    {
        var db = CreateDb();
        var client = CreateClient(db, CreateMintListener(db));

        var result = await client.GetInvoice("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetInvoice_DifferentStore_ReturnsNull()
    {
        var db = CreateDb();
        await using (var ctx = db.CreateContext())
        {
            var invoice = MakeInvoice("inv1");
            invoice.StoreId = "other-store";
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.GetInvoice("inv1");

        Assert.Null(result);
    }
    
    [Fact]
    public async Task ListInvoices_ReturnsAll()
    {
        var db = CreateDb();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.AddRange(
                MakeInvoice("i1", "UNPAID"),
                MakeInvoice("i2", "ISSUED"),
                MakeInvoice("i3", "EXPIRED"));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.ListInvoices();

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public async Task ListInvoices_PendingOnly_ReturnsOnlyUnpaid()
    {
        var db = CreateDb();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.AddRange(
                MakeInvoice("i1", "UNPAID"),
                MakeInvoice("i2", "ISSUED"),
                MakeInvoice("i3", "UNPAID"));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.ListInvoices(new ListInvoicesParams { PendingOnly = true });

        Assert.Equal(2, result.Length);
        Assert.All(result, i => Assert.Equal(LightningInvoiceStatus.Unpaid, i.Status));
    }


    [Fact]
    public async Task GetPayment_ExistingHash_ReturnsPayment()
    {
        var db = CreateDb();
        var hash = "deadbeef";
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningPayments.Add(MakePayment(hash));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.GetPayment(hash);

        Assert.NotNull(result);
        Assert.Equal(hash, result.PaymentHash);
    }

    [Fact]
    public async Task GetPayment_MissingHash_ReturnsNull()
    {
        var db = CreateDb();
        var client = CreateClient(db, CreateMintListener(db));

        var result = await client.GetPayment("nonexistent");

        Assert.Null(result);
    }
    
    [Fact]
    public async Task ListPayments_ReturnsAll()
    {
        var db = CreateDb();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningPayments.AddRange(
                MakePayment("h1", "PAID"),
                MakePayment("h2", "PENDING"),
                MakePayment("h3", "PAID"));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.ListPayments(new ListPaymentsParams { IncludePending = true });

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public async Task ListPayments_ExcludePending_ReturnsOnlyPaid()
    {
        var db = CreateDb();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningPayments.AddRange(
                MakePayment("h1", "PAID"),
                MakePayment("h2", "PENDING"),
                MakePayment("h3", "PAID"));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.ListPayments(new ListPaymentsParams { IncludePending = false });

        Assert.Equal(2, result.Length);
        Assert.All(result, p => Assert.Equal(LightningPaymentStatus.Complete, p.Status));
    }

    [Theory]
    [InlineData("ISSUED", LightningInvoiceStatus.Paid)]
    [InlineData("UNPAID", LightningInvoiceStatus.Unpaid)]
    [InlineData("EXPIRED", LightningInvoiceStatus.Expired)]
    public async Task GetInvoice_QuoteState_MapsToCorrectStatus(string quoteState, LightningInvoiceStatus expected)
    {
        var db = CreateDb();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(MakeInvoice("inv", quoteState));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, CreateMintListener(db));
        var result = await client.GetInvoice("inv");

        Assert.NotNull(result);
        Assert.Equal(expected, result.Status);
    }
}
