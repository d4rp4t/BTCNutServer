using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;

public class CashuTransactionLinkProvider(string blockExplorerLink) : DefaultTransactionLinkProvider(blockExplorerLink)
{
    //cashu transactions are anonymous so there's no explorer, maybe I should provide mint stats from auditor?
    public override string? GetTransactionLink(string paymentId)
    {
        return null;
    }
}