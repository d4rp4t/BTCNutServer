using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests.Integration;

public class MintFixture : IAsyncLifetime
{
    public string CdkMintUrl { get; } =
        Environment.GetEnvironmentVariable("TEST_CDK_MINT_URL") ?? "http://localhost:3338";

    public string NutshellMintUrl { get; } =
        Environment.GetEnvironmentVariable("TEST_NUTSHELL_MINT_URL") ?? "http://localhost:3339";

    public async Task InitializeAsync()
    {
        await WaitForMintAsync(CdkMintUrl, "CDK");
        await WaitForMintAsync(NutshellMintUrl, "Nutshell");
    }

    private static async Task WaitForMintAsync(string mintUrl, string name, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = CashuUtils.GetCashuHttpClient(mintUrl);
                var info = await client.GetInfo();
                if (info != null)
                {
                    Console.WriteLine($"[MintFixture] {name} ready at {mintUrl}");
                    return;
                }
            }
            catch
            {
                // not ready yet
            }
            await Task.Delay(2000);
        }
        throw new TimeoutException($"{name} mint at {mintUrl} not ready after {timeoutSeconds}s");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<MintFixture> { }
