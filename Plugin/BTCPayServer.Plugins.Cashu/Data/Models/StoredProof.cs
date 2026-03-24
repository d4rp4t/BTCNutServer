using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.Cashu.Data.enums;
using DotNut;

namespace BTCPayServer.Plugins.Cashu.Data.Models;

public class StoredProof : Proof
{
    public Guid ProofId { get; set; }
    public string StoreId { get; set; }
    public ProofState Status { get; set; } = ProofState.Available;

    // FK for exported tokens - null means proof is in wallet, set means exported
    public Guid? ExportedTokenId { get; set; }

    public Guid? CashuLightningClientInvoiceId { get; set; }
    public Guid? CashuLightningClientPaymentId { get; set; }

    // EF requires empty constructor
    private StoredProof() { }

    public StoredProof(Proof proof, string storeId, ProofState status)
    {
        this.Id = proof.Id;
        this.Amount = proof.Amount;
        this.Secret = proof.Secret;
        this.C = proof.C;
        this.DLEQ = proof.DLEQ;
        this.Witness = proof.Witness;
        this.StoreId = storeId;
        this.Status = status;
    }

    public Proof ToDotNutProof()
    {
        return new Proof
        {
            Id = this.Id,
            Amount = this.Amount,
            Secret = this.Secret,
            C = this.C,
            DLEQ = this.DLEQ,
            Witness = this.Witness,
        };
    }

    public static IEnumerable<StoredProof> FromBatch(
        IEnumerable<Proof> proofs,
        string storeId,
        ProofState status,
        Guid? paymentId = null
    )
    {
        return proofs.Select(p => new StoredProof(p, storeId, status)
        {
            CashuLightningClientPaymentId = paymentId
        });
    }
}
