using System;

namespace BTCPayServer.Plugins.Cashu.Errors;

/// <summary>
/// Usually it's user who did something wrong
/// </summary>
public class CashuPaymentException : Exception
{
    public CashuPaymentException() { }
    
    public CashuPaymentException(string message)
        : base(message) { }
    
    public CashuPaymentException(string message, Exception inner)
        : base(message, inner) { }
}