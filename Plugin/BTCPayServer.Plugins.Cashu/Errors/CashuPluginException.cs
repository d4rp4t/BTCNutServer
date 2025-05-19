using System;

namespace BTCPayServer.Plugins.Cashu.Errors;

/// <summary>
/// Abstraction for cashu plugin exception. Usually it's not users fault when thrown.
/// </summary>
public class CashuPluginException : Exception
{
    public CashuPluginException() { }
    
    public CashuPluginException(string message)
        : base(message) { }
    
    public CashuPluginException(string message, Exception inner)
        : base(message, inner) { }   
}