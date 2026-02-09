using System;
using System.Linq;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Cashu.Lightning;

public class CashuLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly CashuDbContextFactory _dbContextFactory;
    private readonly MintListener _mintListener;
    private readonly ILoggerFactory _loggerFactory;

    public CashuLightningConnectionStringHandler(CashuDbContextFactory dbContextFactory,
        MintListener mintListener, ILoggerFactory loggerFactory)
    {
        _dbContextFactory = dbContextFactory;
        _mintListener = mintListener;
        _loggerFactory = loggerFactory;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "cashu")
        {
            error = null;
            return null;
        }

        if (!kv.TryGetValue("mint-url", out var url))
        {
            error = "Mint url expected";
            return null;
        }

        if (!kv.TryGetValue("store-id", out var storeId))
        {
            error = "Store Id expected";
            return null;
        }

        Uri uri = new Uri(url);

        bool allowInsecure = false;
        if (kv.TryGetValue("allowinsecure", out var allowinsecureStr))
        {
            var allowedValues = new[] { "true", "false" };
            if (!allowedValues.Any(v => v.Equals(allowinsecureStr, StringComparison.OrdinalIgnoreCase)))
            {
                error = "The key 'allowinsecure' should be true or false";
                return null;
            }

            allowInsecure = allowinsecureStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (!LightningConnectionStringHelper.VerifySecureEndpoint(uri, allowInsecure))
        {
            error = "The key 'allowinsecure' is false, but server's Uri is not using https";
            return null;
        }

        error = null;
        return new CashuLightningClient(uri, storeId, _dbContextFactory, _mintListener, network,
            _loggerFactory.CreateLogger<CashuLightningClient>());
    }
}
