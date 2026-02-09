using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BTCPayServer.Lightning;
using DotNut;
using DotNut.Abstractions;

namespace BTCPayServer.Plugins.Cashu.Data.Models;

public class CashuLightningClientInvoice
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public string Mint { get; set; }
    public string StoreId { get; set; }
    public string QuoteId { get; set; }
    public KeysetId KeysetId { get; set; }
    public List<OutputData> OutputData { get; set; }

    /// <summary>
    /// Raw mint state: "UNPAID", "PAID", "ISSUED":
    /// </summary>
    public string QuoteState { get; set; }

    public string InvoiceId { get; set; }
    public LightMoney Amount { get; set; }
    public string? Bolt11 { get; set; }
    
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Expiry { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    public LightningInvoiceStatus Status
    {
        get
        {
            var status =  QuoteState?.ToUpperInvariant() switch
            {
                // let's assume lightning payment is finished only if we have the proofs.
                "ISSUED" => LightningInvoiceStatus.Paid,
                "PAID" => LightningInvoiceStatus.Unpaid,
                "UNPAID" => LightningInvoiceStatus.Unpaid,
                "EXPIRED" => LightningInvoiceStatus.Expired, // additional state
                _ => LightningInvoiceStatus.Unpaid
            };

            if (status != LightningInvoiceStatus.Unpaid && DateTimeOffset.UtcNow > Expiry)
            {
                return LightningInvoiceStatus.Expired;
            }

            return status;
        }
        private set {}
    }
    public LightningInvoice ToLightningInvoice()
    {
        return new LightningInvoice
        {
            Id = InvoiceId,
            BOLT11 = Bolt11,
            Amount = Amount,
            Status = Status,
            ExpiresAt = Expiry,
            PaidAt = PaidAt,
        };
    }
}
