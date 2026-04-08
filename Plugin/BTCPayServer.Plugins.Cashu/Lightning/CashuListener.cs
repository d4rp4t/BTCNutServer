using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;

namespace BTCPayServer.Plugins.Cashu.Lightning;

public class CashuListener : ILightningInvoiceListener
{
    private readonly MintListener _mintListener;
    private readonly CashuListenerRegistration _registration;

    public CashuListener(MintListener mintListener, string storeId, string mintUrl)
    {
        _mintListener = mintListener;
        _registration = mintListener.RegisterListener(storeId, mintUrl);
    }

    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        return await _registration.NotificationChannel.Reader.ReadAsync(cancellation);
    }

    public void Deliver(LightningInvoice invoice) =>
        _registration.NotificationChannel.Writer.TryWrite(invoice);

    public void Dispose()
    {
        _mintListener.UnregisterListener(_registration.Id);
    }
}
