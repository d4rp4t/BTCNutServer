using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BTCPayServer.Lightning;
using DotNut.Abstractions;

namespace BTCPayServer.Plugins.Cashu.Data.Models;

public class CashuLightningClientPayment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public string StoreId { get; set; }
    public string Mint { get; set; }
    public string QuoteId { get; set; }

    // "UNPAID" | "PENDING" | "PAID" | "EXPIRED"
    // PENDING means lightning payment is in flight and may take a while
    public string QuoteState { get; set; }
    public string PaymentHash { get; set; }
    public string Bolt11 { get; set; }
    public string Preimage { get; set; } = string.Empty;
    public LightMoney Amount { get; set; }
    public LightMoney? FeeAmount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public List<StoredProof> Proofs { get; set; }
    // blank outputs submitted with the melt request — needed to restore change proofs on recovery
    public List<OutputData>? BlankOutputs { get; set; }

    public LightningPayment ToLightningPayment() => new()
    {
        Id = PaymentHash,
        PaymentHash = PaymentHash,
        Preimage = string.IsNullOrEmpty(Preimage) ? null : Preimage,
        Status = QuoteState?.ToUpperInvariant() switch
        {
            "PAID" => LightningPaymentStatus.Complete,
            "PENDING" => LightningPaymentStatus.Pending,
            "UNPAID" => LightningPaymentStatus.Pending,
            _ => LightningPaymentStatus.Unknown
        },
        BOLT11 = Bolt11,
        CreatedAt = CreatedAt,
        Amount = Amount,
        AmountSent = FeeAmount is {} fee ? Amount + fee : Amount,
        Fee = FeeAmount,
    };
}
