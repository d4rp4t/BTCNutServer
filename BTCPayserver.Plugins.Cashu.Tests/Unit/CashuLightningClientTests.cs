using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Lightning;
using DotNut;
using NBitcoin;
using Xunit;
using Mnemonic = DotNut.NBitcoin.BIP39.Mnemonic;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class CashuLightningClientTests
{
    private static readonly Uri FakeMint = new("https://fake-mint.test");
    private const string StoreId = "test-store";
    private static readonly Network TestNetwork = Network.RegTest;
    private static readonly Mnemonic TestMnemonic = new(
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"
    );

    private static CashuLightningClient CreateClient(
        TestDbFactory db,
        MintListener listener,
        string? secret = null
    ) => new(FakeMint, StoreId, secret, db, listener, db.CreateMintManager(), TestNetwork);

    private static async Task SeedWalletConfig(CashuDbContextFactory db, Guid? secret = null)
    {
        await using var ctx = db.CreateContext();
        ctx.CashuWalletConfig.Add(
            new CashuWalletConfig
            {
                StoreId = StoreId,
                WalletMnemonic = TestMnemonic,
                Verified = true,
                LightningClientSecret = secret,
            }
        );
        await ctx.SaveChangesAsync();
    }

    private static CashuLightningClientInvoice MakeInvoice(
        string invoiceId,
        string quoteState = "UNPAID"
    ) =>
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

    private static CashuLightningClientPayment MakePayment(
        string hash,
        string quoteState = "PAID"
    ) =>
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
        var db = TestDbFactory.Create();
        await SeedWalletConfig(db, secret: Guid.NewGuid());
        var client = CreateClient(db, db.CreateMintListener(), secret: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Pay("lnbc...", CancellationToken.None)
        );
    }

    [Fact]
    public async Task Pay_WrongSecret_ThrowsInvalidOperation()
    {
        var db = TestDbFactory.Create();
        await SeedWalletConfig(db, secret: Guid.NewGuid());
        var client = CreateClient(db, db.CreateMintListener(), secret: Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Pay("lnbc...", CancellationToken.None)
        );
    }

    [Fact]
    public async Task Pay_NoWalletConfig_ThrowsInvalidOperation()
    {
        var db = TestDbFactory.Create();
        var client = CreateClient(db, db.CreateMintListener(), secret: Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Pay("lnbc...", CancellationToken.None)
        );
    }

    [Fact]
    public async Task Pay_NoSecret_InConfig_ThrowsInvalidOperation()
    {
        var db = TestDbFactory.Create();
        await SeedWalletConfig(db, secret: null);
        var client = CreateClient(db, db.CreateMintListener(), secret: Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Pay("lnbc...", CancellationToken.None)
        );
    }

    [Fact]
    public async Task GetInvoice_ExistingId_ReturnsInvoice()
    {
        var db = TestDbFactory.Create();
        var invoiceId = "abc123";
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(MakeInvoice(invoiceId));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.GetInvoice(invoiceId);

        Assert.NotNull(result);
        Assert.Equal(invoiceId, result.Id);
    }

    [Fact]
    public async Task GetInvoice_MissingId_ReturnsNull()
    {
        var db = TestDbFactory.Create();
        var client = CreateClient(db, db.CreateMintListener());

        var result = await client.GetInvoice("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetInvoice_DifferentStore_ReturnsNull()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            var invoice = MakeInvoice("inv1");
            invoice.StoreId = "other-store";
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.GetInvoice("inv1");

        Assert.Null(result);
    }

    [Fact]
    public async Task ListInvoices_ReturnsAll()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.AddRange(
                MakeInvoice("i1"),
                MakeInvoice("i2", "ISSUED"),
                MakeInvoice("i3", "EXPIRED")
            );
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListInvoices();

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public async Task ListInvoices_PendingOnly_ReturnsOnlyUnpaid()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.AddRange(
                MakeInvoice("i1"),
                MakeInvoice("i2", "ISSUED"),
                MakeInvoice("i3")
            );
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListInvoices(new ListInvoicesParams { PendingOnly = true });

        Assert.Equal(2, result.Length);
        Assert.All(result, i => Assert.Equal(LightningInvoiceStatus.Unpaid, i.Status));
    }

    [Fact]
    public async Task GetPayment_ExistingHash_ReturnsPayment()
    {
        var db = TestDbFactory.Create();
        var hash = "deadbeef";
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningPayments.Add(MakePayment(hash));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.GetPayment(hash);

        Assert.NotNull(result);
        Assert.Equal(hash, result.PaymentHash);
    }

    [Fact]
    public async Task GetPayment_MissingHash_ReturnsNull()
    {
        var db = TestDbFactory.Create();
        var client = CreateClient(db, db.CreateMintListener());

        var result = await client.GetPayment("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task ListPayments_ReturnsAll()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningPayments.AddRange(
                MakePayment("h1"),
                MakePayment("h2", "PENDING"),
                MakePayment("h3")
            );
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListPayments(new ListPaymentsParams { IncludePending = true });

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public async Task ListPayments_ExcludePending_ReturnsOnlyPaid()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningPayments.AddRange(
                MakePayment("h1"),
                MakePayment("h2", "PENDING"),
                MakePayment("h3")
            );
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListPayments(new ListPaymentsParams { IncludePending = false });

        Assert.Equal(2, result.Length);
        Assert.All(result, p => Assert.Equal(LightningPaymentStatus.Complete, p.Status));
    }

    [Theory]
    [InlineData("ISSUED", LightningInvoiceStatus.Paid)]
    [InlineData("UNPAID", LightningInvoiceStatus.Unpaid)]
    [InlineData("EXPIRED", LightningInvoiceStatus.Expired)]
    public async Task GetInvoice_QuoteState_MapsToCorrectStatus(
        string quoteState,
        LightningInvoiceStatus expected
    )
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(MakeInvoice("inv", quoteState));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.GetInvoice("inv");

        Assert.NotNull(result);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void ToString_WithSecret_ContainsSecretPart()
    {
        var db = TestDbFactory.Create();
        var secret = Guid.NewGuid().ToString();
        var client = CreateClient(db, db.CreateMintListener(), secret: secret);

        var str = client.ToString();

        Assert.Contains("type=cashu", str);
        Assert.Contains($"mint-url={FakeMint}", str);
        Assert.Contains($"store-id={StoreId}", str);
        Assert.Contains($"secret={secret}", str);
    }

    [Fact]
    public void ToString_WithoutSecret_NoSecretPart()
    {
        var db = TestDbFactory.Create();
        var client = CreateClient(db, db.CreateMintListener(), secret: null);

        var str = client.ToString();

        Assert.Contains("type=cashu", str);
        Assert.DoesNotContain("secret=", str);
    }

    [Fact]
    public async Task GetPayment_DifferentStore_ReturnsNull()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            var p = MakePayment("hash1");
            p.StoreId = "other-store";
            ctx.LightningPayments.Add(p);
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.GetPayment("hash1");

        Assert.Null(result);
    }

    [Fact]
    public async Task ListPayments_OnlyReturnsOwnStore()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            var foreign = MakePayment("h2");
            foreign.StoreId = "other-store";
            ctx.LightningPayments.AddRange(MakePayment("h1"), foreign);
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListPayments(new ListPaymentsParams { IncludePending = true });

        Assert.Single(result);
        Assert.Equal("h1", result[0].PaymentHash);
    }

    [Fact]
    public async Task ListInvoices_OnlyReturnsOwnStore()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            var foreign = MakeInvoice("i2");
            foreign.StoreId = "other-store";
            ctx.LightningInvoices.AddRange(MakeInvoice("i1"), foreign);
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListInvoices();

        Assert.Single(result);
        Assert.Equal("i1", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_PendingOnly_IncludesPaidState()
    {
        // PAID = ln payment received by mint, tokens not yet issued — still "pending" from ours perspective
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(MakeInvoice("i1", "PAID"));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListInvoices(new ListInvoicesParams { PendingOnly = true });

        Assert.Single(result);
    }

    [Fact]
    public async Task ListInvoices_PendingOnly_ExcludesIssuedInvoices()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.AddRange(MakeInvoice("i1"), MakeInvoice("i2", "ISSUED"));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListInvoices(new ListInvoicesParams { PendingOnly = true });

        Assert.Single(result);
        Assert.Equal("i1", result[0].Id);
    }

    [Fact]
    public async Task ListInvoices_PendingOnly_ExcludesExpiredInvoices()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            var expired = MakeInvoice("i1");
            expired.Expiry = DateTimeOffset.UtcNow.AddSeconds(-60);
            ctx.LightningInvoices.Add(expired);
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListInvoices(new ListInvoicesParams { PendingOnly = true });

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListPayments_DefaultParams_IncludesPendingPayments()
    {
        var db = TestDbFactory.Create();
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningPayments.AddRange(MakePayment("h1"), MakePayment("h2", "PENDING"));
            await ctx.SaveChangesAsync();
        }

        var client = CreateClient(db, db.CreateMintListener());
        var result = await client.ListPayments();

        Assert.Equal(2, result.Length);
    }
}
