using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.Cashu.Data.Models;

namespace BTCPayServer.Plugins.Cashu.ViewModels;

public class CashuWalletViewModel
{
   public List<(string Mint, string Unit, ulong Amount)> AvaibleBalances { get; set; }
   public List<ExportedToken> ExportedTokens { get; set; }
   
   public IEnumerable<(ulong Amount, string Unit)> GroupedBalances => AvaibleBalances
       .GroupBy(b => b.Unit)
       .OrderByDescending(g => g.Key)
       .Select(gr => ((ulong)gr.Sum(x => (decimal)x.Amount), gr.Key));
   
}