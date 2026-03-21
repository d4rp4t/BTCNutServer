using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Errors;
using BTCPayServer.Plugins.Cashu.PaymentHandlers;
using DotNut;
using DotNut.ApiModels;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class FeeValidationTests
{
    private const string ValidPubKeyHex =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private static readonly KeysetId KeysetA = new("000000000000001a");
    private static readonly KeysetId KeysetB = new("000000000000001b");

    private static Proof MakeProof(ulong amount, KeysetId keysetId) =>
        new()
        {
            Amount = amount,
            Id = keysetId,
            Secret = new StringSecret(Guid.NewGuid().ToString()),
            C = new PubKey(ValidPubKeyHex),
        };

    private static GetKeysetsResponse.KeysetItemResponse MakeKeyset(
        KeysetId id,
        ulong? inputFee = null
    ) =>
        new()
        {
            Id = id,
            Unit = "sat",
            Active = true,
            InputFee = inputFee,
        };

    private static CashuFeeConfig DefaultFeeConfig(
        int maxKeysetFee = 5,
        int maxLightningFee = 5,
        int customerFeeAdvance = 0
    ) =>
        new()
        {
            MaxKeysetFee = maxKeysetFee,
            MaxLightningFee = maxLightningFee,
            CustomerFeeAdvance = customerFeeAdvance,
        };

    [Fact]
    public void ValidateFees_EmptyProofList_ReturnsFalse()
    {
        var result = CashuUtils.ValidateFees([], DefaultFeeConfig(), [MakeKeyset(KeysetA)], out _);

        Assert.False(result);
    }

    [Fact]
    public void ValidateFees_UnknownKeyset_Throws()
    {
        var proofs = new List<Proof> { MakeProof(1000, KeysetA) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetB), // wrong keyset
        };

        Assert.Throws<CashuPaymentException>(() =>
            CashuUtils.ValidateFees(proofs, DefaultFeeConfig(), keysets, out _)
        );
    }

    [Fact]
    public void ValidateFees_ZeroInputFee_ReturnsTrue()
    {
        var proofs = new List<Proof> { MakeProof(1000, KeysetA) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 0),
        };

        var result = CashuUtils.ValidateFees(proofs, DefaultFeeConfig(), keysets, out var fee);

        Assert.True(result);
        Assert.Equal(0UL, fee);
    }

    [Fact]
    public void ValidateFees_NullInputFee_TreatedAsZero()
    {
        var proofs = new List<Proof> { MakeProof(500, KeysetA) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: null),
        };

        var result = CashuUtils.ValidateFees(proofs, DefaultFeeConfig(), keysets, out var fee);

        Assert.True(result);
        Assert.Equal(0UL, fee);
    }

    [Fact]
    public void ValidateFees_FeeWithinCustomerAdvance_ReturnsTrue()
    {
        var proofs = new List<Proof> { MakeProof(1000, KeysetA) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 100),
        };
        var config = DefaultFeeConfig(maxKeysetFee: 0, maxLightningFee: 5, customerFeeAdvance: 2);

        var result = CashuUtils.ValidateFees(proofs, config, keysets, out _);

        Assert.True(result);
    }

    [Fact]
    public void ValidateFees_FeeExceedsCustomerAdvanceAndMaxPercent_ReturnsFalse()
    {
        var proofs = Enumerable.Range(0, 10).Select(_ => MakeProof(10, KeysetA)).ToList();
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 1000),
        };
        var config = DefaultFeeConfig(maxKeysetFee: 1, maxLightningFee: 5, customerFeeAdvance: 0);

        var result = CashuUtils.ValidateFees(proofs, config, keysets, out _);

        Assert.False(result);
    }

    [Fact]
    public void ValidateFees_FeeExactlyAtMaxPercent_ReturnsTrue()
    {
        var proofs = new List<Proof> { MakeProof(1000, KeysetA) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 0),
        };
        var config = DefaultFeeConfig(maxKeysetFee: 0, maxLightningFee: 5, customerFeeAdvance: 0);

        var result = CashuUtils.ValidateFees(proofs, config, keysets, out _);

        Assert.True(result);
    }

    [Fact]
    public void ValidateFees_LightningFeeWithinLimit_ReturnsTrue()
    {
        var proofs = new List<Proof> { MakeProof(1000, KeysetA) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 0),
        };
        var config = DefaultFeeConfig(maxKeysetFee: 5, maxLightningFee: 5, customerFeeAdvance: 0);

        var result = CashuUtils.ValidateFees(proofs, config, keysets, out _, feeReserve: 10);

        Assert.True(result);
    }

    [Fact]
    public void ValidateFees_LightningFeeExceedsLimit_ReturnsFalse()
    {
        var proofs = new List<Proof> { MakeProof(100, KeysetA) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 0),
        };
        var config = DefaultFeeConfig(maxKeysetFee: 5, maxLightningFee: 1, customerFeeAdvance: 0);

        var result = CashuUtils.ValidateFees(proofs, config, keysets, out _, feeReserve: 100);

        Assert.False(result);
    }

    [Fact]
    public void ValidateFees_OutputsCalculatedKeysetFee()
    {
        var proofs = new List<Proof>
        {
            MakeProof(64, KeysetA),
            MakeProof(128, KeysetA),
            MakeProof(256, KeysetA),
        };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 1000),
        };
        var config = DefaultFeeConfig(
            maxKeysetFee: 100,
            maxLightningFee: 100,
            customerFeeAdvance: 0
        );

        CashuUtils.ValidateFees(proofs, config, keysets, out var fee);

        Assert.Equal(3UL, fee);
    }

    [Fact]
    public void ValidateFees_MultipleKeysets_ComputesFeeCorrectly()
    {
        var proofs = new List<Proof> { MakeProof(100, KeysetA), MakeProof(100, KeysetB) };
        var keysets = new List<GetKeysetsResponse.KeysetItemResponse>
        {
            MakeKeyset(KeysetA, inputFee: 1000),
            MakeKeyset(KeysetB, inputFee: 2000),
        };
        var config = DefaultFeeConfig(
            maxKeysetFee: 100,
            maxLightningFee: 100,
            customerFeeAdvance: 0
        );

        var result = CashuUtils.ValidateFees(proofs, config, keysets, out var fee);

        Assert.True(result);
        Assert.Equal(3UL, fee);
    }
}
