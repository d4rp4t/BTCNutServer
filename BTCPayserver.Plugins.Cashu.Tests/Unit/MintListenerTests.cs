using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Data.enums;
using DotNut;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class MintListenerTests(ITestOutputHelper output)
{
    private const string StoreId = "test-store";
    private const string MintUrl = "https://fake-mint.test";
    private const string ValidPubKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private static Proof MakeProof() => new()
    {
        Amount = 100,
        Id = new KeysetId("000000000000001a"),
        Secret = new StringSecret(Guid.NewGuid().ToString()),
        C = new PubKey(ValidPubKeyHex),
    };

    private static CashuLightningClientInvoice MakeInvoice(
        string quoteState = "UNPAID",
        DateTimeOffset? expiry = null) => new()
    {
        StoreId = StoreId,
        Mint = MintUrl.TrimEnd('/'),
        QuoteId = Guid.NewGuid().ToString(),
        InvoiceId = Guid.NewGuid().ToString("N"),
        KeysetId = new KeysetId("0000000000000001"),
        OutputData = [],
        QuoteState = quoteState,
        Amount = LightMoney.Satoshis(100),
        Bolt11 = "lnbc...",
        Created = DateTimeOffset.UtcNow,
        Expiry = expiry ?? DateTimeOffset.UtcNow.AddHours(1),
    };


    [Fact]
    public void RegisterListener_ReturnsRegistrationWithUniqueId()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        var r1 = listener.RegisterListener(StoreId, MintUrl);
        var r2 = listener.RegisterListener(StoreId, MintUrl);

        Assert.NotEqual(r1.Id, r2.Id);
    }

    [Fact]
    public void RegisterListener_SetsCorrectStoreAndMint()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        var reg = listener.RegisterListener(StoreId, MintUrl);

        Assert.Equal(StoreId, reg.StoreId);
        Assert.Equal(MintUrl, reg.MintUrl);
    }

    [Fact]
    public void UnregisterListener_CompletesChannel()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        var reg = listener.RegisterListener(StoreId, MintUrl);
        listener.UnregisterListener(reg.Id);

        Assert.True(reg.NotificationChannel.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void UnregisterListener_UnknownId_DoesNotThrow()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        listener.UnregisterListener("non-existent-id"); // should not throw
    }


    [Fact]
    public void GetPayLock_SameStoreId_ReturnsSameSemaphore()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        var lock1 = listener.GetPayLock("store-a");
        var lock2 = listener.GetPayLock("store-a");

        Assert.Same(lock1, lock2);
    }

    [Fact]
    public void GetPayLock_DifferentStoreIds_ReturnDifferentSemaphores()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        var lockA = listener.GetPayLock("store-a");
        var lockB = listener.GetPayLock("store-b");

        Assert.NotSame(lockA, lockB);
    }


    [Fact]
    public void TrackPendingPayment_DoesNotThrow()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        listener.TrackPendingPayment(Guid.NewGuid()); // should not throw
    }

    [Fact]
    public void TrackPendingPayment_SameIdTwice_DoesNotThrow()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);

        var id = Guid.NewGuid();
        listener.TrackPendingPayment(id);
        listener.TrackPendingPayment(id); // idempotent
    }


    [Fact]
    public async Task Cleanup_ExpiredUnpaidQuote_MarksAsExpired()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);
        await listener.StartAsync(CancellationToken.None);

        var invoice = MakeInvoice("UNPAID", DateTimeOffset.UtcNow.AddSeconds(-1));
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        // CleanupInterval = 3s in tests, wait long enough
        await Task.Delay(TimeSpan.FromSeconds(5));

        await using (var ctx = db.CreateContext())
        {
            var updated = await ctx.LightningInvoices.FirstAsync(i => i.QuoteId == invoice.QuoteId);
            Assert.Equal("EXPIRED", updated.QuoteState);
        }

        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cleanup_NonExpiredUnpaidQuote_RemainsUnpaid()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);
        await listener.StartAsync(CancellationToken.None);

        var invoice = MakeInvoice("UNPAID", DateTimeOffset.UtcNow.AddHours(1));
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(5));

        await using (var ctx = db.CreateContext())
        {
            var updated = await ctx.LightningInvoices.FirstAsync(i => i.QuoteId == invoice.QuoteId);
            Assert.Equal("UNPAID", updated.QuoteState);
        }

        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cleanup_IssuedQuote_NotTouched()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener(output);
        await listener.StartAsync(CancellationToken.None);

        // ISSUED quote with expired timestamp - cleanup should not change it
        var invoice = MakeInvoice("ISSUED", DateTimeOffset.UtcNow.AddSeconds(-1));
        invoice.Proofs = [new StoredProof(MakeProof(), StoreId, ProofState.Available)];
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(5));

        await using (var ctx = db.CreateContext())
        {
            var updated = await ctx.LightningInvoices.FirstAsync(i => i.QuoteId == invoice.QuoteId);
            Assert.Equal("ISSUED", updated.QuoteState);
        }

        await listener.StopAsync(CancellationToken.None);
    }


    [Fact]
    public async Task Listen_AlreadyIssuedInvoice_DeliveredImmediately()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);

        var invoice = MakeInvoice("ISSUED");
        invoice.Proofs = [new StoredProof(MakeProof(), StoreId, ProofState.Available)];
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        var client = new BTCPayServer.Plugins.Cashu.Lightning.CashuLightningClient(
            new Uri(MintUrl),
            StoreId,
            null,
            db,
            mintListener,
            db.CreateMintManager(),
            NBitcoin.Network.RegTest
        );

        var invoiceListener = await client.Listen();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = await invoiceListener.WaitInvoice(cts.Token);

        Assert.Equal(invoice.InvoiceId, received.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, received.Status);

        invoiceListener.Dispose();
    }

    [Fact]
    public async Task Listen_NoIssuedInvoices_DoesNotDeliverAnything()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);

        // Only UNPAID invoice in DB
        var invoice = MakeInvoice("UNPAID");
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        var client = new BTCPayServer.Plugins.Cashu.Lightning.CashuLightningClient(
            new Uri(MintUrl),
            StoreId,
            null,
            db,
            mintListener,
            db.CreateMintManager(),
            NBitcoin.Network.RegTest
        );

        var invoiceListener = await client.Listen();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoiceListener.WaitInvoice(cts.Token));

        invoiceListener.Dispose();
    }

    [Fact]
    public async Task Listen_IssuedWithoutProofs_NotDelivered()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);

        // ISSUED but Proofs is null - should not be delivered
        var invoice = MakeInvoice("ISSUED");
        invoice.Proofs = null!;
        await using (var ctx = db.CreateContext())
        {
            ctx.LightningInvoices.Add(invoice);
            await ctx.SaveChangesAsync();
        }

        var client = new BTCPayServer.Plugins.Cashu.Lightning.CashuLightningClient(
            new Uri(MintUrl),
            StoreId,
            null,
            db,
            mintListener,
            db.CreateMintManager(),
            NBitcoin.Network.RegTest
        );

        var invoiceListener = await client.Listen();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoiceListener.WaitInvoice(cts.Token));

        invoiceListener.Dispose();
    }
}
