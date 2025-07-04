using BTCPayServer.Plugins.Cashu.CashuAbstractions;

namespace BTCPayServer.Plugins.Cashu.ViewModels;

public class ExportedTokenViewModel
{
    public string Token { get; set; }
    public ulong Amount { get; set; }
    public string Unit { get; set; }
    public string MintAddress { get; set; }
    
    public string FormatedAmount
    {
        get
        {
            var result = CashuUtils.FormatAmount(this.Amount, this.Unit);
            return $"{result.Amount} {result.Unit}";
        }
    }
}

