using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using DotNut.ApiModels;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration;

[Collection("Integration")]
[Trait("Category", "Integration")]
public class MintInfoTests(MintFixture fixture)
{
    [Theory]
    [InlineData("cdk")]
    [InlineData("nutshell")]
    public async Task GetMintInfo_ReturnsInfo(string mintType)
    {
        var url = mintType == "cdk" ? fixture.CdkMintUrl : fixture.NutshellMintUrl;
        using var client = CashuUtils.GetCashuHttpClient(url);

        var info = await client.GetInfo();

        Assert.NotNull(info);
    }

    [Theory]
    [InlineData("cdk")]
    [InlineData("nutshell")]
    public async Task GetKeysets_ReturnsActiveKeyset(string mintType)
    {
        var url = mintType == "cdk" ? fixture.CdkMintUrl : fixture.NutshellMintUrl;
        using var client = CashuUtils.GetCashuHttpClient(url);

        var keysets = await client.GetKeysets();

        Assert.NotNull(keysets);
        Assert.NotEmpty(keysets.Keysets);
        Assert.Contains(keysets.Keysets, k => k.Active == true);
    }

    [Theory]
    [InlineData("cdk")]
    [InlineData("nutshell")]
    public async Task CreateMintQuote_ReturnsBolt11Invoice(string mintType)
    {
        var url = mintType == "cdk" ? fixture.CdkMintUrl : fixture.NutshellMintUrl;
        using var client = CashuUtils.GetCashuHttpClient(url);
        var req = new PostMintQuoteBolt11Request() { Amount = 100UL, Unit = "sat" };
        var quote = await client.CreateMintQuote<
            PostMintQuoteBolt11Response,
            PostMintQuoteBolt11Request
        >("bolt11", req);

        Assert.NotNull(quote);
        Assert.NotEmpty(quote.Quote);
        Assert.NotEmpty(quote.Request);
        Assert.StartsWith("ln", quote.Request); // BOLT11 starts with "ln"
    }
}
