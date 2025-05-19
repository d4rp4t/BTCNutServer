using System;

namespace BTCPayServer.Plugins.Cashu.Errors;

public class MintUnavaibleError: Exception
{
    public MintUnavaibleError()
    {
        
    }
    
    public MintUnavaibleError(string message)
        : base(message) { }
    
    public MintUnavaibleError(string message, Exception inner)
        : base(message, inner) { }   
}