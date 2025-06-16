using BTCPayServer.Plugins.Cashu.Data.enums;

namespace BTCPayServer.Plugins.Cashu.ViewModels;

public class CashuStoreViewModel
{
    public bool Enabled { get; set; }

    public CashuPaymentModel PaymentAcceptanceModel { get; set; } // "SwapAndHodl", "MeltImmediately"

    public string TrustedMintsUrls { get; set; } // newline-separated URLs, with trailing slash remoed
    
    //in percent too (I think in 1% would be normal)
    public int MaxKeysetFee { get; set; }
    
    //in percent - I'd advice to use at least 2-3%. Many mints support overpaid return, but it's not mandatory!
    public int MaxLightningFee { get; set; }
    
    //in sats - estimated fee that user pays for us in order to cover fee expenses, it will affect amount 
    public int CustomerFeeAdvance { get; set; }
    
}
