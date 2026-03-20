using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Errors;
using BTCPayServer.Plugins.Cashu.PaymentHandlers;
using DotNut;
using DotNut.Api;
using DotNut.ApiModels;
using NBitcoin;
using Utils = DotNut.Abstractions.Utils;

namespace BTCPayServer.Plugins.Cashu.CashuAbstractions;

public static class CashuUtils
{
    /// <summary>
    /// Factory for cashu client - creates new httpclient for given mint
    /// </summary>
    /// <param name="mintUrl"></param>
    /// <returns></returns>
    public static CashuHttpClient GetCashuHttpClient(string mintUrl)
    {
        //add trailing / so mint like https://mint.minibits.cash/Bitcoin will work correctly
        var mintUri = new Uri(mintUrl + "/");
        var client = new HttpClient { BaseAddress = mintUri };
        //Some operations, like Melt can take a long time. But 5 minutes should be more than ok.
        client.Timeout = TimeSpan.FromMinutes(5);
        var cashuClient = new CashuHttpClient(client, true);
        return cashuClient;
    }

    /// <summary>
    /// Calculate token worth - by requesting its mint quote for one proof its unit
    /// </summary>
    /// <param name="token">Encoded Cashu Token</param>
    /// <param name="network"></param>
    /// <returns>Token's worth</returns>
    public static async Task<LightMoney> GetTokenSatRate(CashuToken token, Network network)
    {
        var simplifiedToken = SimplifyToken(token);

        return await GetTokenSatRate(simplifiedToken.Mint, simplifiedToken.Unit ?? "sat", Network.Main);
    }

    public static async Task<LightMoney> GetTokenSatRate(string mint, string unit, Network network)
    {
        if (String.IsNullOrWhiteSpace(mint))
        {
            throw new ArgumentNullException(nameof(mint));
        }

        if (String.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentNullException(nameof(unit));
        }

        var cashuClient = GetCashuHttpClient(mint);

        var mintQuote = await cashuClient.CreateMintQuote<
            PostMintQuoteBolt11Response,
            PostMintQuoteBolt11Request
        >("bolt11", new PostMintQuoteBolt11Request { Amount = 1000, Unit = unit });
        var paymentRequest = mintQuote.Request;

        if (
            !BOLT11PaymentRequest.TryParse(
                paymentRequest,
                out var parsedPaymentRequest,
                network
            )
        )
        {
            throw new Exception("Invalid BOLT11 payment request.");
        }

        if (parsedPaymentRequest == null)
        {
            throw new NullReferenceException($"Invalid payment request: {paymentRequest}");
        }

        return parsedPaymentRequest.MinimumAmount / 1000;
    }

    /// <summary>
    /// Factory for Simplified version of cashu token.
    /// </summary>
    /// <param name="token">CashuToken</param>
    /// <exception cref="CashuPaymentException"></exception>
    /// <returns>Simplified Cashu Token</returns>
    public static SimplifiedCashuToken SimplifyToken(CashuToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(token.Tokens);

        if (token.Tokens.GroupBy(t => t.Mint).Count() != 1)
        {
            throw new CashuPaymentException("Only single-mint tokens (v4) are supported.");
        }

        var proofs = token.Tokens.SelectMany(t => t.Proofs).ToList();
        var firstToken = token.Tokens.FirstOrDefault();
        if (firstToken == null)
        {
            throw new CashuPaymentException("Token contains no mint information.");
        }

        return new SimplifiedCashuToken
        {
            Mint = MintManager.NormalizeMintUrl(firstToken.Mint),
            Proofs = proofs,
            Memo = token.Memo,
            Unit = token.Unit ?? "sat",
        };
    }

    /// <summary>
    /// Function selecting proofs to send and to keep from provided inputAmounts.
    /// </summary>
    /// <param name="inputAmounts"></param>
    /// <param name="keyset"></param>
    /// <param name="requestedAmont"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static (List<ulong> keep, List<ulong> send) SplitAmountsForPayment(
        List<ulong> inputAmounts,
        Keyset keyset,
        ulong requestedAmont
    )
    {
        if (requestedAmont > inputAmounts.Aggregate((a, c) => a + c))
        {
            throw new InvalidOperationException("Requested amount is greater than input amounts.");
        }

        if (inputAmounts.Any(ia => !keyset.Keys.Contains(ia)))
        {
            throw new InvalidOperationException("Keyset don't support provided amounts.");
        }

        var change = inputAmounts.Aggregate((a, c) => a + c) - requestedAmont;
        var sendAmounts = Utils.SplitToProofsAmounts(requestedAmont, keyset);
        if (change == 0)
        {
            return (new List<ulong>(), sendAmounts);
        }

        var keepAmounts = Utils.SplitToProofsAmounts(change, keyset);

        return (keepAmounts, sendAmounts);
    }

    /// <summary>
    /// Helper function creating NUT-18 payment request
    /// </summary>
    /// <param name="amount">Amount</param>
    /// <param name="invoiceId">Payment id. In this scenario invoice id.</param>
    /// <param name="endpoint">POST request endpoint. for now only http post supported</param>
    /// <param name="trustedMintsUrls">list of merchants trusted mints</param>
    /// <returns>serialized payment request</returns>
    public static string CreatePaymentRequest(
        Money amount,
        string invoiceId,
        string endpoint,
        IEnumerable<string>? trustedMintsUrls
    )
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new ArgumentNullException(nameof(endpoint));
        }

        if (string.IsNullOrEmpty(invoiceId))
        {
            throw new ArgumentNullException(nameof(invoiceId));
        }

        if (amount < Money.Zero)
        {
            throw new ArgumentException("Amount must be greater than 0.");
        }

        var paymentRequest = new DotNut.PaymentRequest()
        {
            Unit = "sat", //since it's not standardized how to denominate tokens, it will always be sats.
            Amount = amount == Money.Zero ? null : (ulong)amount.Satoshi,
            PaymentId = invoiceId,
            Mints = trustedMintsUrls?.ToArray() ?? [],
            Transports = [new PaymentRequestTransport { Type = "post", Target = endpoint }],
        };
        return paymentRequest.ToString();
    }

    /// <summary>
    /// Expands short 16-char v1 keyset IDs in proofs to their full 66-char form using the list of known keysets.
    /// Token v4 (cashuB) stores keyset IDs in shortened form to save space; this must be resolved before fee validation.
    /// </summary>
    public static List<Proof> ExpandShortKeysetIds(
        List<Proof> proofs,
        List<GetKeysetsResponse.KeysetItemResponse> keysets
    )
    {
        if (proofs.All(p => p.Id.GetVersion() != 0x01 || p.Id.ToString().Length != 16))
            return proofs;

        var keysetIds = keysets.Select(k => k.Id).ToList();
        return proofs
            .Select(proof =>
            {
                if (proof.Id.GetVersion() != 0x01 || proof.Id.ToString().Length != 16)
                    return proof;

                var shortId = proof.Id.ToString();
                var match = keysetIds.FirstOrDefault(k =>
                    k.ToString().StartsWith(shortId, StringComparison.OrdinalIgnoreCase)
                );

                if (match is null)
                    throw new CashuPaymentException(
                        $"Unknown keyset ID {shortId} for this mint"
                    );

                return new Proof
                {
                    Amount = proof.Amount,
                    Secret = proof.Secret,
                    C = proof.C,
                    Witness = proof.Witness,
                    DLEQ = proof.DLEQ,
                    Id = match,
                };
            })
            .ToList();
    }

    /// <summary>
    /// Helper function validating maximum allowed fees, so malicious mint can't rug us and trick us into receiving payment with too big keyset fee.
    /// </summary>
    /// <param name="proofs">Proofs we want to spend</param>
    /// <param name="config">CashuFeeConfig</param>
    /// <param name="keysets">Keysets</param>
    /// <param name="keysetFee">Calculated keyset fee</param>
    /// <param name="feeReserve"></param>
    /// <returns></returns>
    public static bool ValidateFees(
        List<Proof> proofs,
        CashuFeeConfig config,
        List<GetKeysetsResponse.KeysetItemResponse> keysets,
        out ulong keysetFee,
        ulong feeReserve = 0
    )
    {
        keysetFee = 0;
        if (proofs.Count == 0)
            return false;

        var keysetsUsed = proofs.Select(p => p.Id).Distinct().ToList();

        if (!keysetsUsed.All(k => keysets.Any(ks => ks.Id == k)))
            throw new CashuPaymentException("Unknown keysets for this mint!");

        var keysetFeesDict = keysets
            .Where(k => keysetsUsed.Contains(k.Id))
            .ToDictionary(k => k.Id, k => k.InputFee ?? 0UL);

        keysetFee = proofs.ComputeFee(keysetFeesDict);

        ulong totalAmount = proofs.Select(p => p.Amount).Sum();

        decimal maximumKeysetFee = Math.Ceiling(config.MaxKeysetFee / 100m * totalAmount);
        decimal maximumLightningFee = Math.Ceiling(config.MaxLightningFee / 100m * totalAmount);

        // underflow safety
        long feeAdvanceDiff = (long)keysetFee - config.CustomerFeeAdvance;
        if (feeAdvanceDiff < 0)
            feeAdvanceDiff = 0;

        if (feeAdvanceDiff > maximumKeysetFee)
            return false;

        long lightningFeeDiff = (long)feeReserve - (config.CustomerFeeAdvance - (long)keysetFee);
        if (lightningFeeDiff < 0)
            lightningFeeDiff = 0;

        if (lightningFeeDiff > maximumLightningFee)
            return false;

        return true;
    }

    /// <summary>
    /// ValidateFees overload for cases, where fees are already calculated
    /// </summary>
    /// <param name="proofs"></param>
    /// <param name="config"></param>
    /// <param name="keysetFee"></param>
    /// <param name="feeReserve"></param>
    /// <returns></returns>
    public static bool ValidateFees(
        List<Proof> proofs,
        CashuFeeConfig config,
        ulong keysetFee,
        ulong feeReserve = 0
    )
    {
        if (proofs.Count == 0)
            return false;

        ulong totalAmount = proofs.Select(p => p.Amount).Sum();

        decimal maximumKeysetFee = Math.Ceiling(config.MaxKeysetFee / 100m * totalAmount);
        decimal maximumLightningFee = Math.Ceiling(config.MaxLightningFee / 100m * totalAmount);

        long feeAdvanceDiff = (long)keysetFee - config.CustomerFeeAdvance;
        if (feeAdvanceDiff < 0)
            feeAdvanceDiff = 0;

        if (feeAdvanceDiff > maximumKeysetFee)
            return false;

        long lightningFeeDiff = (long)feeReserve - (config.CustomerFeeAdvance - (long)keysetFee);
        if (lightningFeeDiff < 0)
            lightningFeeDiff = 0;

        if (lightningFeeDiff > maximumLightningFee)
            return false;

        return true;
    }

    public class SimplifiedCashuToken
    {
        public string Mint { get; set; }
        public List<Proof> Proofs { get; set; }
        public string? Memo { get; set; }
        public string Unit { get; set; }

        public ulong SumProofs => Proofs?.Select(p => p.Amount).Sum() ?? 0;
    }

    public static bool TryDecodeToken(string token, out CashuToken? cashuToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            cashuToken = null;
            return false;
        }

        try
        {
            cashuToken = CashuTokenHelper.Decode(token, out _);
            return true;
        }
        catch (Exception)
        {
            //do nothing, token is invalid
        }

        cashuToken = null;
        return false;
    }

    /// <summary>
    /// Formating method specified in NUT-1 based on ISO 4217.
    /// Only UI tweak, shouldn't trust mint with its unit.
    /// </summary>
    /// <param name="amount">Proofs amount</param>
    /// <param name="unit">Proofs unit</param>
    /// <returns>Formatted amount and unit</returns>
    public static (decimal Amount, string Unit) FormatAmount(decimal amount, string unit = "sat")
    {
        unit = string.IsNullOrWhiteSpace(unit) ? "SAT" : unit.ToUpperInvariant();

        var bitcoinUnits = new Dictionary<string, int>
        {
            { "BTC", 8 },
            { "SAT", 0 },
            { "MSAT", 3 },
        };

        if (bitcoinUnits.TryGetValue(unit, out var minorUnit))
        {
            decimal adjusted = amount / (decimal)Math.Pow(10, minorUnit);
            return (adjusted, unit);
        }

        var specialMinorUnits = new Dictionary<string, int>
        {
            { "BHD", 3 },
            { "BIF", 0 },
            { "CLF", 4 },
            { "CLP", 0 },
            { "DJF", 0 },
            { "GNF", 0 },
            { "IQD", 3 },
            { "ISK", 0 },
            { "JOD", 3 },
            { "JPY", 0 },
            { "KMF", 0 },
            { "KRW", 0 },
            { "KWD", 3 },
            { "LYD", 3 },
            { "OMR", 3 },
            { "PYG", 0 },
            { "RWF", 0 },
            { "TND", 3 },
            { "UGX", 0 },
            { "UYI", 0 },
            { "UYW", 4 },
            { "VND", 0 },
            { "VUV", 0 },
            { "XAF", 0 },
            { "XOF", 0 },
            { "XPF", 0 },
        };

        int fiatMinor = specialMinorUnits.ContainsKey(unit) ? specialMinorUnits[unit] : 2;
        decimal fiatAdjusted = amount / (decimal)Math.Pow(10, fiatMinor);

        return (fiatAdjusted, unit);
    }
}
