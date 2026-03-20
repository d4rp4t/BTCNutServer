using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using DotNut;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class MintManagerTests
{
    private const string ValidPubKeyHex =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private static Keyset MakeKeyset() => new() { [1] = new PubKey(ValidPubKeyHex) };
    private static KeysetId SomeKeysetId() => new("000000000000001a");

    private static async Task SeedMintWithKeyset(
        CashuDbContextFactory db, string mintUrl, KeysetId keysetId)
    {
        await using var ctx = db.CreateContext();
        var mint = new Mint(mintUrl);
        ctx.Mints.Add(mint);
        await ctx.SaveChangesAsync();
        ctx.MintKeys.Add(new MintKeys
        {
            MintId = mint.Id,
            Mint = mint,
            KeysetId = keysetId,
            Unit = "sat",
            Keyset = MakeKeyset(),
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public void NormalizeMintUrl_TrimsTrailingSlash()
    {
        var result = MintManager.NormalizeMintUrl("https://mint.example.com/");
        Assert.Equal("https://mint.example.com", result);
    }

    [Theory]
    [InlineData("https://mint.example.com", "https://mint.example.com")]
    [InlineData("https://mint.example.com/", "https://mint.example.com")]
    [InlineData("https://mint.example.com/path", "https://mint.example.com/path")]
    [InlineData("HTTPS://MINT.EXAMPLE.COM", "https://mint.example.com")]
    public void NormalizeMintUrl_ProducesConsistentUrls(string input, string expected)
    {
        Assert.Equal(expected, MintManager.NormalizeMintUrl(input));
    }

    [Fact]
    public async Task GetOrCreateMint_NewMint_CreatesMint()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        var mint = await manager.GetOrCreateMint("https://mint.test/");

        Assert.NotNull(mint);
        Assert.NotEqual(0, mint.Id);

        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.Mints.CountAsync());
    }

    [Fact]
    public async Task GetOrCreateMint_ExistingMint_ReturnsSameMint()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        var first = await manager.GetOrCreateMint("https://mint.test/");
        var second = await manager.GetOrCreateMint("https://mint.test/");

        Assert.Equal(first.Id, second.Id);

        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.Mints.CountAsync());
    }

    [Fact]
    public async Task GetOrCreateMint_NormalizesUrl_TreatsAsTheSameMint()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        var a = await manager.GetOrCreateMint("HTTPS://MINT.TEST");
        var b = await manager.GetOrCreateMint("https://mint.test/");

        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public async Task MintExists_ExistingMint_ReturnsTrue()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        await manager.GetOrCreateMint("https://mint.test/");

        Assert.True(await manager.MintExists("https://mint.test/"));
    }

    [Fact]
    public async Task MintExists_NonExistingMint_ReturnsFalse()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        Assert.False(await manager.MintExists("https://unknown.test/"));
    }

    [Fact]
    public async Task MintExists_NormalizesUrl()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        await manager.GetOrCreateMint("https://mint.test/");

        Assert.True(await manager.MintExists("HTTPS://MINT.TEST"));
    }

    [Fact]
    public async Task SaveKeyset_NewKeyset_SavesSuccessfully()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        var keysetId = SomeKeysetId();

        await manager.SaveKeyset("https://mint.test/", keysetId, MakeKeyset(), "sat");

        await using var ctx = db.CreateContext();
        Assert.True(await ctx.MintKeys.AnyAsync(mk => mk.KeysetId == keysetId));
    }

    [Fact]
    public async Task SaveKeyset_SameKeysetSameMint_IsIdempotent()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        var keysetId = SomeKeysetId();

        await manager.SaveKeyset("https://mint.test/", keysetId, MakeKeyset(), "sat");
        await manager.SaveKeyset("https://mint.test/", keysetId, MakeKeyset(), "sat");

        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.MintKeys.CountAsync(mk => mk.KeysetId == keysetId));
    }

    [Fact]
    public async Task SaveKeyset_KeysetBelongsToAnotherMint_Throws()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        var keysetId = SomeKeysetId();

        await manager.SaveKeyset("https://mint-a.test/", keysetId, MakeKeyset(), "sat");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SaveKeyset("https://mint-b.test/", keysetId, MakeKeyset(), "sat"));
    }

    [Fact]
    public async Task SaveKeyset_CreatesMintIfNotExists()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        await manager.SaveKeyset("https://new-mint.test/", SomeKeysetId(), MakeKeyset(), "sat");

        await using var ctx = db.CreateContext();
        Assert.True(await ctx.Mints.AnyAsync(m => m.Url == "https://new-mint.test/"));
    }

    [Fact]
    public async Task GetKeysetInfo_ExistingKeyset_ReturnsInfo()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        var keysetId = SomeKeysetId();
        await manager.SaveKeyset("https://mint.test/", keysetId, MakeKeyset(), "sat");

        var info = await manager.GetKeysetInfo(keysetId);

        Assert.NotNull(info);
        Assert.Equal("https://mint.test/", info!.Value.MintUrl);
        Assert.Equal("sat", info.Value.Unit);
    }

    [Fact]
    public async Task GetKeysetInfo_NonExistingKeyset_ReturnsNull()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        var info = await manager.GetKeysetInfo(SomeKeysetId());

        Assert.Null(info);
    }

    [Fact]
    public async Task MapKeysetIdsToMints_ReturnsMapping()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        var id1 = new KeysetId("000000000000001a");
        var id2 = new KeysetId("000000000000001b");
        await manager.SaveKeyset("https://mint-a.test/", id1, MakeKeyset(), "sat");
        await manager.SaveKeyset("https://mint-b.test/", id2, MakeKeyset(), "usd");

        var map = await manager.MapKeysetIdsToMints([id1, id2]);

        Assert.Equal(2, map.Count);
        Assert.Equal("https://mint-a.test/", map[id1.ToString()].MintUrl);
        Assert.Equal("https://mint-b.test/", map[id2.ToString()].MintUrl);
        Assert.Equal("usd", map[id2.ToString()].Unit);
    }

    [Fact]
    public async Task MapKeysetIdsToMints_EmptyInput_ReturnsEmpty()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        var map = await manager.MapKeysetIdsToMints([]);

        Assert.Empty(map);
    }

    [Fact]
    public async Task MapKeysetIdsToMints_UnknownKeysetId_NotInResult()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        var map = await manager.MapKeysetIdsToMints([SomeKeysetId()]);

        Assert.Empty(map);
    }

    [Fact]
    public async Task ValidateKeysetOwnership_MintNotInDb_DoesNotThrow()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);

        await manager.ValidateKeysetOwnership("https://new-mint.test/", [SomeKeysetId()]);
    }

    [Fact]
    public async Task ValidateKeysetOwnership_KeysetsBelongToSameMint_DoesNotThrow()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        var keysetId = SomeKeysetId();
        await SeedMintWithKeyset(db, "https://mint.test/", keysetId);

        await manager.ValidateKeysetOwnership("https://mint.test/", [keysetId]);
    }

    [Fact]
    public async Task ValidateKeysetOwnership_KeysetBelongsToOtherMint_Throws()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        var keysetId = SomeKeysetId();
        await SeedMintWithKeyset(db, "https://mint-a.test/", keysetId);
        await manager.GetOrCreateMint("https://mint-b.test/");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ValidateKeysetOwnership("https://mint-b.test/", [keysetId]));
    }

    [Fact]
    public async Task ValidateKeysetOwnership_EmptyKeysetList_DoesNotThrow()
    {
        var db = TestDbFactory.Create();
        var manager = new MintManager(db);
        await manager.GetOrCreateMint("https://mint.test/");

        await manager.ValidateKeysetOwnership("https://mint.test/", []);
    }
}
