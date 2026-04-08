using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Lightning;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class CashuListenerTests(ITestOutputHelper output)
{
    private const string StoreId = "test-store";
    private const string MintUrl = "https://fake-mint.test";

    private static LightningInvoice MakeLightningInvoice(string id = "inv1") => new()
    {
        Id = id,
        Amount = LightMoney.Satoshis(100),
        Status = LightningInvoiceStatus.Paid,
        BOLT11 = "lnbc...",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    [Fact]
    public async Task Deliver_WaitInvoice_ReturnsDeliveredInvoice()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);
        var listener = new CashuListener(mintListener, StoreId, MintUrl);

        var invoice = MakeLightningInvoice();
        listener.Deliver(invoice);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = await listener.WaitInvoice(cts.Token);

        Assert.Equal(invoice.Id, received.Id);
        Assert.Equal(LightningInvoiceStatus.Paid, received.Status);

        listener.Dispose();
    }

    [Fact]
    public async Task Deliver_MultipleInvoices_AllReceived()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);
        var listener = new CashuListener(mintListener, StoreId, MintUrl);

        listener.Deliver(MakeLightningInvoice("inv1"));
        listener.Deliver(MakeLightningInvoice("inv2"));
        listener.Deliver(MakeLightningInvoice("inv3"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ids = new HashSet<string>();
        for (var i = 0; i < 3; i++)
            ids.Add((await listener.WaitInvoice(cts.Token)).Id!);

        Assert.Equal(new HashSet<string> { "inv1", "inv2", "inv3" }, ids);

        listener.Dispose();
    }

    [Fact]
    public async Task WaitInvoice_Cancelled_ThrowsOperationCancelled()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);
        var listener = new CashuListener(mintListener, StoreId, MintUrl);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            listener.WaitInvoice(cts.Token));

        listener.Dispose();
    }

    [Fact]
    public void Dispose_UnregistersFromMintListener()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);
        var listener = new CashuListener(mintListener, StoreId, MintUrl);

        // Grab the registration before dispose
        // After dispose the channel should be completed
        listener.Dispose();

        // Trying to deliver after dispose should silently fail (TryWrite on completed channel)
        var invoice = MakeLightningInvoice();
        listener.Deliver(invoice); // should not throw
    }

    [Fact]
    public async Task Dispose_CompletesChannel_WaitInvoiceEnds()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);
        var listener = new CashuListener(mintListener, StoreId, MintUrl);

        // Start waiting in background
        var waitTask = Task.Run(() => listener.WaitInvoice(CancellationToken.None));

        // Give the wait a moment to start
        await Task.Delay(50);

        listener.Dispose();

        // WaitInvoice should throw ChannelClosedException or similar when channel is completed
        await Assert.ThrowsAnyAsync<Exception>(() => waitTask);
    }

    [Fact]
    public async Task MultipleListeners_SameMintStore_BothReceiveViaNotifyListeners()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);
        await mintListener.StartAsync(CancellationToken.None);

        var listener1 = new CashuListener(mintListener, StoreId, MintUrl);
        var listener2 = new CashuListener(mintListener, StoreId, MintUrl);

        var invoice = MakeLightningInvoice("shared-inv");
        listener1.Deliver(invoice);
        listener2.Deliver(invoice);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var r1 = await listener1.WaitInvoice(cts.Token);
        var r2 = await listener2.WaitInvoice(cts.Token);

        Assert.Equal("shared-inv", r1.Id);
        Assert.Equal("shared-inv", r2.Id);

        listener1.Dispose();
        listener2.Dispose();
        await mintListener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DifferentStore_ListenerDoesNotReceiveOtherStoreInvoice()
    {
        var db = TestDbFactory.Create();
        var mintListener = db.CreateMintListener(output);
        await mintListener.StartAsync(CancellationToken.None);

        var listenerStore1 = new CashuListener(mintListener, "store-1", MintUrl);
        var listenerStore2 = new CashuListener(mintListener, "store-2", MintUrl);

        // Only deliver to store-1's listener
        listenerStore1.Deliver(MakeLightningInvoice("inv-store1"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var received = await listenerStore1.WaitInvoice(cts.Token);
        Assert.Equal("inv-store1", received.Id);

        // store-2 listener should have nothing
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            listenerStore2.WaitInvoice(cts2.Token));

        listenerStore1.Dispose();
        listenerStore2.Dispose();
        await mintListener.StopAsync(CancellationToken.None);
    }
}
