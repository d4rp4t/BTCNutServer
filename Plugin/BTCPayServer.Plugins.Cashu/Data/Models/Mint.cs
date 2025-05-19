using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BTCPayServer.Plugins.Cashu.Data.Models;

public class Mint
{
    public Mint(string url)
    {
        this.Url = url;
    }
    
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    
    public string Url { get; set; }
    
    public ICollection<MintKeys> Keysets { get; }
}