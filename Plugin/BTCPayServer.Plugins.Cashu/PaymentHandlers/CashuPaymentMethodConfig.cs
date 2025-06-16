using System.Collections.Generic;
using BTCPayServer.Plugins.Cashu.Data.enums;

namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;

public class CashuPaymentMethodConfig
{
    public CashuPaymentModel PaymentModel { get; set; }
    
    public List<string> TrustedMintsUrls { get; set; }
    
    public CashuFeeConfig FeeConfing { get; set; }
}

public class CashuFeeConfig
{
    //in %
    public int MaxKeysetFee { get; set; }
    
    //in %
    public int MaxLightningFee { get; set; }
    
    //in sats - estimated fee that user pays for us in order to cover fee expenses. Added as Tweak Fee
    public int CustomerFeeAdvance { get; set; }
}