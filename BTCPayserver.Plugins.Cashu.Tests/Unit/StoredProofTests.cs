using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class StoredProofTests
{
    private const string ValidPubKeyHex =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private static Proof MakeProof(
        ulong amount = 64,
        string? secret = null,
        string? witness = null
    ) =>
        new()
        {
            Amount = amount,
            Id = new KeysetId("000000000000001a"),
            Secret = new StringSecret(secret ?? Guid.NewGuid().ToString()),
            C = new PubKey(ValidPubKeyHex),
            Witness = witness,
        };

    [Fact]
    public void Constructor_CopiesAllProofFields()
    {
        var proof = MakeProof(amount: 128, secret: "mysecret", witness: "mywitness");

        var stored = new StoredProof(proof, "store1", ProofState.Available);

        Assert.Equal(proof.Amount, stored.Amount);
        Assert.Equal(proof.Id, stored.Id);
        Assert.Equal(proof.Secret, stored.Secret);
        Assert.Equal(proof.C.ToString(), stored.C.ToString());
        Assert.Equal(proof.Witness, stored.Witness);
    }

    [Fact]
    public void Constructor_SetsStoreIdAndStatus()
    {
        var stored = new StoredProof(MakeProof(), "mystore", ProofState.Reserved);

        Assert.Equal("mystore", stored.StoreId);
        Assert.Equal(ProofState.Reserved, stored.Status);
    }

    [Fact]
    public void Constructor_DefaultsExportedTokenIdToNull()
    {
        var stored = new StoredProof(MakeProof(), "store", ProofState.Available);

        Assert.Null(stored.ExportedTokenId);
        Assert.Null(stored.CashuLightningClientInvoiceId);
        Assert.Null(stored.CashuLightningClientPaymentId);
    }

    [Fact]
    public void Constructor_NullWitness_IsPreserved()
    {
        var proof = MakeProof(witness: null);
        var stored = new StoredProof(proof, "store", ProofState.Available);

        Assert.Null(stored.Witness);
    }

    [Fact]
    public void ToDotNutProof_ReturnsEquivalentProof()
    {
        var proof = MakeProof(amount: 256, secret: "abc", witness: "sig");
        var stored = new StoredProof(proof, "store", ProofState.Available);

        var result = stored.ToDotNutProof();

        Assert.Equal(proof.Amount, result.Amount);
        Assert.Equal(proof.Id, result.Id);
        Assert.Equal(proof.C.ToString(), result.C.ToString());
        Assert.Equal(proof.Witness, result.Witness);
    }

    [Fact]
    public void ToDotNutProof_ResultIsNotStoredProof()
    {
        var stored = new StoredProof(MakeProof(), "store", ProofState.Available);

        var result = stored.ToDotNutProof();

        Assert.IsNotType<StoredProof>(result);
        Assert.IsType<Proof>(result);
    }

    [Fact]
    public void ToDotNutProof_DoesNotIncludeStoreId()
    {
        var stored = new StoredProof(MakeProof(), "sensitive-store-id", ProofState.Available);
        var result = stored.ToDotNutProof();

        Assert.IsNotType<StoredProof>(result);
    }

    [Fact]
    public void FromBatch_ReturnsAllProofsAsStoredProofs()
    {
        var proofs = new List<Proof> { MakeProof(8), MakeProof(16), MakeProof(32) };

        var result = StoredProof.FromBatch(proofs, "store", ProofState.Available).ToList();

        Assert.Equal(3, result.Count);
        Assert.All(result, sp => Assert.Equal("store", sp.StoreId));
        Assert.All(result, sp => Assert.Equal(ProofState.Available, sp.Status));
    }

    [Fact]
    public void FromBatch_WithPaymentId_SetsPaymentIdOnAll()
    {
        var paymentId = Guid.NewGuid();
        var proofs = new List<Proof> { MakeProof(), MakeProof(128) };

        var result = StoredProof
            .FromBatch(proofs, "store", ProofState.Reserved, paymentId)
            .ToList();

        Assert.All(result, sp => Assert.Equal(paymentId, sp.CashuLightningClientPaymentId));
    }

    [Fact]
    public void FromBatch_WithoutPaymentId_PaymentIdIsNull()
    {
        var proofs = new List<Proof> { MakeProof() };

        var result = StoredProof.FromBatch(proofs, "store", ProofState.Available).ToList();

        Assert.All(result, sp => Assert.Null(sp.CashuLightningClientPaymentId));
    }

    [Fact]
    public void FromBatch_EmptyList_ReturnsEmpty()
    {
        var result = StoredProof.FromBatch([], "store", ProofState.Available).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void FromBatch_PreservesAmountsAndIds()
    {
        var proofs = new List<Proof> { MakeProof(8), MakeProof(), MakeProof(512) };

        var result = StoredProof.FromBatch(proofs, "store", ProofState.Spent).ToList();

        Assert.Equal(new ulong[] { 8, 64, 512 }, result.Select(sp => sp.Amount).ToArray());
        Assert.All(result, sp => Assert.Equal(ProofState.Spent, sp.Status));
    }

    [Fact]
    public void GetPayLock_SameStore_ReturnsSameSemaphore()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener();

        var lock1 = listener.GetPayLock("store-a");
        var lock2 = listener.GetPayLock("store-a");

        Assert.Same(lock1, lock2);
    }

    [Fact]
    public void GetPayLock_DifferentStores_ReturnDifferentSemaphores()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener();

        var lockA = listener.GetPayLock("store-a");
        var lockB = listener.GetPayLock("store-b");

        Assert.NotSame(lockA, lockB);
    }

    [Fact]
    public void GetPayLock_InitialCount_IsOne()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener();

        var semaphore = listener.GetPayLock("store");

        Assert.Equal(1, semaphore.CurrentCount);
    }
}
