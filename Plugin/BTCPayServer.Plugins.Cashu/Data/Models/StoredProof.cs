using System;
using System.Collections.Generic;
using System.Linq;
using DotNut;

namespace BTCPayServer.Plugins.Cashu.Data.Models;

public class StoredProof : DotNut.Proof
{
    public Guid ProofId { get; set; }
    public string StoreId { get; set; }
    //entity framework will cry without empty constructor
    private StoredProof() {} 

    public StoredProof(Proof proof, string storeId) 
    {
        this.Id = proof.Id;
        this.Amount = proof.Amount;
        this.Secret = proof.Secret;
        this.C = proof.C;
        this.DLEQ = proof.DLEQ;
        this.Witness = proof.Witness;
        this.StoreId = storeId;
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
            Witness = this.Witness
        };
    }
    public static IEnumerable<StoredProof> FromBatch(IEnumerable<Proof> proofs, string storeId)
    {
        return proofs.Select(p=>new StoredProof(p, storeId));
    }
    
    
}