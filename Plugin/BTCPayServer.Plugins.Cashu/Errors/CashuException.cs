using System;

namespace BTCPayServer.Plugins.Cashu.Errors;

/// <summary>
/// Bcase class for all Cashu plugin exception
/// Subclasses:
///   CashuPaymentException - user error, safe to show message
///   CashuPluginException - system error, show generic message to user
/// </summary>
public abstract class CashuException : Exception
{
    protected CashuException(string message)
        : base(message) { }

    protected CashuException(string message, Exception inner)
        : base(message, inner) { }
}
