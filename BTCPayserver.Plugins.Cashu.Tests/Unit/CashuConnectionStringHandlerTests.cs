#nullable enable
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Lightning;
using NBitcoin;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Unit;

public class CashuConnectionStringHandlerTests
{
    private static CashuLightningConnectionStringHandler CreateHandler()
    {
        var db = TestDbFactory.Create();
        var listener = db.CreateMintListener();
        var manager = new MintManager(db);
        return new CashuLightningConnectionStringHandler(db, listener, manager);
    }

    [Fact]
    public void Create_MissingMintUrl_ReturnsNullWithError()
    {
        var handler = CreateHandler();

        var client = handler.Create("type=cashu;store-id=store1", Network.RegTest, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("Mint url", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_MissingStoreId_ReturnsNullWithError()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=https://mint.test/",
            Network.RegTest,
            out var error
        );

        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("Store Id", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HttpMintUrl_WithoutAllowInsecure_ReturnsError()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=http://mint.test/;store-id=store1",
            Network.RegTest,
            out var error
        );

        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("allowinsecure", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HttpMintUrl_WithAllowInsecureTrue_ReturnsClient()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=http://mint.test/;store-id=store1;allowinsecure=true",
            Network.RegTest,
            out var error
        );

        Assert.NotNull(client);
        Assert.Null(error);
    }

    [Fact]
    public void Create_HttpMintUrl_AllowInsecureFalse_ReturnsError()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=http://mint.test/;store-id=store1;allowinsecure=false",
            Network.RegTest,
            out var error
        );

        Assert.Null(client);
        Assert.NotNull(error);
    }

    [Fact]
    public void Create_InvalidAllowInsecureValue_ReturnsError()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=https://mint.test/;store-id=store1;allowinsecure=maybe",
            Network.RegTest,
            out var error
        );

        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("allowinsecure", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_ValidHttps_ReturnsClientAndNoError()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=https://mint.test/;store-id=store1",
            Network.RegTest,
            out var error
        );

        Assert.NotNull(client);
        Assert.Null(error);
        Assert.IsType<CashuLightningClient>(client);
    }

    [Fact]
    public void Create_WithSecret_ClientToStringIncludesSecret()
    {
        var handler = CreateHandler();
        var secret = Guid.NewGuid().ToString();

        var client = handler.Create(
            $"type=cashu;mint-url=https://mint.test/;store-id=store1;secret={secret}",
            Network.RegTest,
            out var error
        );

        Assert.NotNull(client);
        Assert.Null(error);
        Assert.Contains(secret, client.ToString());
    }

    [Fact]
    public void Create_WithoutSecret_ClientToStringExcludesSecretKey()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=https://mint.test/;store-id=store1",
            Network.RegTest,
            out var error
        );

        Assert.NotNull(client);
        Assert.Null(error);
        Assert.DoesNotContain("secret=", client.ToString());
    }

    [Fact]
    public void Create_AllowInsecureCaseInsensitive_Accepted()
    {
        var handler = CreateHandler();

        var client = handler.Create(
            "type=cashu;mint-url=http://mint.test/;store-id=store1;allowinsecure=TRUE",
            Network.RegTest,
            out var error
        );

        Assert.NotNull(client);
        Assert.Null(error);
    }

    [Fact]
    public void ClientToString_WithSecret_ContainsAllParts()
    {
        var handler = CreateHandler();
        var secret = Guid.NewGuid().ToString();

        var client = (CashuLightningClient)
            handler.Create(
                $"type=cashu;mint-url=https://mint.test/;store-id=mystore;secret={secret}",
                Network.RegTest,
                out _
            )!;

        var str = client.ToString();
        Assert.Contains("type=cashu", str);
        Assert.Contains("mint-url=", str);
        Assert.Contains("store-id=mystore", str);
        Assert.Contains($"secret={secret}", str);
    }

    [Fact]
    public void ClientToString_WithoutSecret_NoSecretPart()
    {
        var handler = CreateHandler();

        var client = (CashuLightningClient)
            handler.Create(
                "type=cashu;mint-url=https://mint.test/;store-id=mystore",
                Network.RegTest,
                out _
            )!;

        var str = client.ToString();
        Assert.Contains("type=cashu", str);
        Assert.Contains("store-id=mystore", str);
        Assert.DoesNotContain("secret=", str);
    }
}
