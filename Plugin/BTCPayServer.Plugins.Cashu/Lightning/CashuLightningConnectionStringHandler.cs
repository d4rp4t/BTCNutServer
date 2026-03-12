#nullable enable
using System;
using System.Linq;
using AngleSharp.Common;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using NBitcoin;

namespace BTCPayServer.Plugins.Cashu.Lightning;

public class CashuLightningConnectionStringHandler(
    CashuDbContextFactory dbContextFactory,
    MintListener mintListener)
    : ILightningConnectionStringHandler
{
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

        var secret = kv.GetOrDefault("secret", null);

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
        return new CashuLightningClient(uri, storeId, secret, dbContextFactory, mintListener, network);
    }
}
