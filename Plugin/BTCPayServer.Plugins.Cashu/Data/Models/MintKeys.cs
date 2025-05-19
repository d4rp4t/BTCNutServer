using DotNut;


namespace BTCPayServer.Plugins.Cashu.Data.Models;

public record MintKeys
{
    public int MintId { get; set; }
    public Mint Mint { get; set; }
    public KeysetId KeysetId { get; set; }
    
    public string Unit { get; set; }
    public Keyset Keyset {get;set;}

}