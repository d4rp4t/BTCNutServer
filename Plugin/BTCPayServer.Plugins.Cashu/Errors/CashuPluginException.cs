using System;

namespace BTCPayServer.Plugins.Cashu.Errors;

/// <summary>
/// System-level plugin error - not the user's fault. Message should NOT be shown to the customer as-is
/// </summary>
public class CashuPluginException : CashuException
{
    public CashuPluginException(string message)
        : base(message) { }

    public CashuPluginException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Mint is unreachable (network error, DNS failure, timeout)
/// </summary>
public class MintUnreachableException : CashuPluginException
{
    public string MintUrl { get; }

    public MintUnreachableException(string mintUrl, Exception? inner = null)
        : base($"Mint unreachable: {mintUrl}", inner!)
    {
        MintUrl = mintUrl;
    }
}

/// <summary>
/// Lightning node is not configured or unavailable for the store
/// </summary>
public class LightningUnavailableException : CashuPluginException
{
    public LightningUnavailableException(string message = "Lightning client is not configured.")
        : base(message) { }

    public LightningUnavailableException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Swap or melt operation failed at the mint level
/// </summary>
public class MintOperationException : CashuPluginException
{
    public MintOperationException(string message)
        : base(message) { }

    public MintOperationException(string message, Exception inner)
        : base(message, inner) { }
}
