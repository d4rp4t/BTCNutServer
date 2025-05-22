using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;

namespace BTCPayServer.Plugins.Cashu.Data.Models;

public class FailedTransaction
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }
    public required string InvoiceId { get; set; }
    public required string StoreId { get; set; }
    public required string MintUrl { get; set; }
    public required string Unit { get; set; }
    public List<StoredProof> UsedProofs { get; set; }
    
    public required OperationType OperationType { get; set; }
    //For melt operation these will be for fee return. For swap these will contain outputs sent to mint. 
    public required CashuUtils.OutputData OutputData { get; set; }
    public MeltDetails? MeltDetails { get; set; }
    public required int RetryCount { get; set; }
    public required DateTimeOffset LastRetried {get;set;}
    public string Details {get;set;}
    public bool Resolved { get; set; }
    
  

    
}

public class MeltDetails
{
    public required string MeltQuoteId { get; set; }
    public required DateTimeOffset Expiry { get; set; }
    public required string LightningInvoiceId { get; set; }
    public required string Status { get; set; }

}


public enum OperationType
{
    Swap,
    Melt
}