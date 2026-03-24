using System;

namespace BTCPayServer.Plugins.Cashu.Errors;

/// <summary>
/// User-facing payment error - safe to show the message to the customer
/// </summary>
public class CashuPaymentException : CashuException
{
    public CashuPaymentException(string message)
        : base(message) { }

    public CashuPaymentException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Token submitted by the user is malformed or undecodable
/// </summary>
public class InvalidTokenException : CashuPaymentException
{
    public InvalidTokenException(string message = "Invalid token.")
        : base(message) { }

    public InvalidTokenException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Token value is less than the invoice amount
/// </summary>
public class InsufficientFundsException : CashuPaymentException
{
    public long ExpectedSats { get; }
    public long ProvidedSats { get; }

    public InsufficientFundsException(long expectedSats, long providedSats)
        : base($"Insufficient token value. Expected {expectedSats} sat, got {providedSats} sat.")
    {
        ExpectedSats = expectedSats;
        ProvidedSats = providedSats;
    }
}

/// <summary>
/// Token comes from a mint that is not in the merchant's trusted list
/// </summary>
public class UntrustedMintException : CashuPaymentException
{
    public string MintUrl { get; }

    public UntrustedMintException(string mintUrl)
        : base("Payment from this mint is not supported. Please contact the merchant to resolve this issue.")
    {
        MintUrl = mintUrl;
    }
}

/// <summary>
/// Keyset or lightning fees exceed the merchant's configured limits
/// </summary>
public class FeesTooHighException : CashuPaymentException
{
    public FeesTooHighException(string message = "Fees exceed the configured limit.")
        : base(message) { }
}

/// <summary>
/// Keyset ID in the token conflicts with known keysets - possible collision attack
/// </summary>
public class KeysetConflictException : CashuPaymentException
{
    public KeysetConflictException(string message = "Token rejected: keyset ID conflict detected. Funds were not spent.")
        : base(message) { }

    public KeysetConflictException(string message, Exception inner)
        : base(message, inner) { }
}
