#nullable enable
using System;
using DotNut.NBitcoin.BIP39;

namespace BTCPayServer.Plugins.Cashu.Data.Models;

public class CashuWalletConfig
{
    public required string StoreId { get; set; }
    public required  Mnemonic WalletMnemonic { get; set; }
    public bool Verified { get; set; } = false;
    
    public Guid? LightningClientSecret { get; set; } = null;
}
