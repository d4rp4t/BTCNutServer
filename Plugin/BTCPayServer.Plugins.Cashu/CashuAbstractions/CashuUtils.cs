using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Errors;
using BTCPayServer.Plugins.Cashu.PaymentHandlers;
using DotNut;
using DotNut.Api;
using DotNut.ApiModels;
using NBitcoin;
using NBitcoin.Secp256k1;
using DLEQProof = DotNut.DLEQProof;

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
        var cashuClient = new CashuHttpClient(client);
        return cashuClient;
    }

    /// <summary>
    /// Calculate token worth - by requesting its mint quote for one proof its unit
    /// </summary>
    /// <param name="token">Encoded Cashu Token</param>
    /// <param name="network"></param>
    /// <returns>Token's worth in satoshi</returns>
    public static async Task<decimal> GetTokenSatRate(CashuToken token, Network network)
    {
        var simplifiedToken = SimplifyToken(token);

        return await GetTokenSatRate(simplifiedToken.Mint, simplifiedToken.Unit ?? "sat", network);
    }

    public static async Task<decimal> GetTokenSatRate(string mint, string unit, Network network)
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

        var mintQuote = await cashuClient.CreateMintQuote<PostMintQuoteBolt11Response, PostMintQuoteBolt11Request>(
            "bolt11",
            new PostMintQuoteBolt11Request { Amount = 1000, Unit = unit });
        var paymentRequest = mintQuote.Request;


        if (!BOLT11PaymentRequest.TryParse(paymentRequest, out var parsedPaymentRequest, network))
        {
            throw new Exception("Invalid BOLT11 payment request.");
        }

        if (parsedPaymentRequest == null)
        {
            throw new NullReferenceException($"Invalid payment request: {paymentRequest}");
        }

        return parsedPaymentRequest.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi) / 1000;
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

        return new SimplifiedCashuToken
        {
            Mint = token.Tokens.First().Mint,
            Proofs = proofs,
            Memo = token.Memo,
            Unit = token.Unit ?? "sat"
        };
    }

    /// <summary>
    /// Function choosing which proofs have to be spent in order to provide the correct value.
    /// </summary>
    /// <param name="proofs">User proofs</param>
    /// <param name="amountToSend">Amount (in tokens unit!!)</param>
    /// <returns>SendResponse containing proofs to keep and proofs to send.</returns>
    public static SendResponse SelectProofsToSend(List<Proof> proofs, ulong amountToSend)
    {
        // Sort proofs in ascending order by amount
        var sortedProofs = proofs.OrderBy(p => p.Amount).ToList();

        // Separate proofs into two lists: smaller or equal to amountToSend, and bigger
        var smallerProofs = sortedProofs
            .Where(p => p.Amount <= amountToSend)
            .OrderByDescending(p => p.Amount).ToList();
        var biggerProofs = sortedProofs
            .Where(p => p.Amount > amountToSend)
            .OrderBy(p => p.Amount).ToList();
        var nextBigger = biggerProofs.FirstOrDefault();

        // If no smaller proofs exist but a bigger proof is available, send the bigger one
        if (!smallerProofs.Any() && nextBigger != null)
            return new SendResponse
            {
                Keep = proofs.Where(p => p.Secret != nextBigger.Secret).ToList(),
                Send = [nextBigger]
            };

        // If no valid proofs are available, return all proofs as Keep
        if (!smallerProofs.Any() && nextBigger == null)
            return new SendResponse { Keep = proofs, Send = new List<Proof>() };

        // Start selecting proofs with the largest possible proof first (it can be the exact amount)
        var remainder = amountToSend;
        var selectedProofs = new List<Proof> { smallerProofs[0] };

        // Reduce the remainder amount by the selected proof amount
        remainder -= selectedProofs[0].Amount;

        // Recursively select additional proofs if needed
        if (remainder > 0)
        {
            var recursiveResponse = SelectProofsToSend(smallerProofs.Skip(1).ToList(), remainder);
            selectedProofs.AddRange(recursiveResponse.Send);
        }

        // If the selected proofs do not sum up to the required amount, use the next bigger proof
        if (selectedProofs.Select(p => p.Amount).Sum() < amountToSend && nextBigger != null)
            selectedProofs = [nextBigger];
        else if (selectedProofs.Select(p => p.Amount).Sum() < amountToSend && nextBigger == null)
        {
            selectedProofs = new List<Proof>();
        }

        // Return the selected proofs for sending and the remaining proofs to keep
        return new SendResponse
        {
            Keep = proofs.Where(p => selectedProofs.All(sp => !sp.Secret.GetBytes().SequenceEqual(p.Secret.GetBytes())))
                .ToList(),
            Send = selectedProofs
        };
    }

    /// <summary>
    /// Function mapping payment amount to keyset supported amounts in order to create swap payload. Always tries to fit the biggest proof.
    /// </summary>
    /// <param name="paymentAmount">Amount that has to be covered.</param>
    /// <param name="keyset">Mints keyset></param>
    /// <returns>List of ulong proof amounts for given keyset</returns>
    public static List<ulong> SplitToProofsAmounts(ulong paymentAmount, Keyset keyset)
    {
        var outputAmounts = new List<ulong>();
        var possibleValues = keyset.Keys.OrderByDescending(x => x).ToList();
        foreach (var value in possibleValues)
        {
            while (paymentAmount >= value)
            {
                outputAmounts.Add(value);
                paymentAmount -= value;
            }

            if (paymentAmount == 0)
            {
                break;
            }
        }

        return outputAmounts;
    }

    /// <summary>
    /// Function selecting proofs to send and to keep from provided inputAmounts. 
    /// </summary>
    /// <param name="inputAmounts"></param>
    /// <param name="keyset"></param>
    /// <param name="requestedAmont"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static (List<ulong> keep, List<ulong> send) SplitAmountsForPayment(List<ulong> inputAmounts, Keyset keyset,
        ulong requestedAmont)
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
        var sendAmounts = SplitToProofsAmounts(requestedAmont, keyset);
        if (change == 0)
        {
            return (new List<ulong>(), sendAmounts);
        }

        var keepAmounts = SplitToProofsAmounts(change, keyset);

        return (keepAmounts, sendAmounts);
    }

    /// <summary>
    /// Creates blank outputs (see nut-08)
    /// </summary>
    /// <param name="amount">Amount that blank outputs have to cover</param>
    /// <param name="keysetId">Active keyset id which will sign outputs</param>
    /// <param name="keys">Keys for given KeysetId</param>
    /// <returns>Blank Outputs</returns>
    public static OutputData CreateBlankOutputs(ulong amount, KeysetId keysetId, Keyset keys)
    {
        if (amount == 0)
        {
            throw new ArgumentException("Cannot create blank outputs zero amount.");
        }

        var count = CalculateNumberOfBlankOutputs(amount);

        // Amount is set for 1, they're blank. Mint will automatically set their amount and sign each by pk corresponding to value
        var amounts = Enumerable.Repeat((ulong)1, count).ToList();
        return CreateOutputs(amounts, keysetId, keys);
    }

    /// <summary>
    /// Creates outputs for swap/melt fee return. Outputs should have valid amounts. 
    /// </summary>
    /// <param name="amounts">Amounts for each output (e.g. [1,2,4,8]</param>
    /// <param name="keysetId">ID of keyset we want to receive the proofs</param>
    /// <param name="keys">Keyset for given ID</param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static OutputData CreateOutputs(List<ulong> amounts, KeysetId keysetId, Keyset keys)
    {
        var blindedMessages = new List<BlindedMessage>();
        var secrets = new List<DotNut.ISecret>();
        var blindingFactors = new List<PrivKey>();

        if (amounts.Any(a => !keys.Keys.Contains(a)))
        {
            throw new ArgumentException("Invalid amounts");
        }

        foreach (var t in amounts)
        {
            //secrets
            //for now create StringSecret. In the future, Nut10Secret may be implemented.
            var secretBytes = RandomNumberGenerator.GetBytes(32);
            var secret = new StringSecret(Convert.ToHexString(secretBytes));
            secrets.Add(secret);

            //blinding factor
            var r = new PrivKey(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            blindingFactors.Add(r);

            //blindedMessage
            var B_ = DotNut.Cashu.ComputeB_(secret.ToCurve(), r);
            blindedMessages.Add(new BlindedMessage() { Amount = t, B_ = B_, Id = keysetId });
        }

        return new OutputData()
        {
            BlindingFactors = blindingFactors.ToArray(),
            BlindedMessages = blindedMessages.ToArray(),
            Secrets = secrets.ToArray()
        };
    }


    /// <summary>
    /// Calculates amount of blank outputs needed by mint to return overpaid fees
    /// </summary>
    /// <param name="amountToCover">Amount of tokens that has to be covered by mint.</param>
    /// <returns>Integer amount of blank outputs needed</returns>
    /// <exception cref="Exception">If amount is 0 - idk why someone would do that</exception>
    private static int CalculateNumberOfBlankOutputs(ulong amountToCover)
    {
        if (amountToCover == 0)
        {
            return 0;
        }

        return Math.Max(
            Convert.ToInt32(
                Math.Ceiling(
                    Math.Log2(amountToCover)
                )
            ), 1);
    }

    /// <summary>
    /// Removes dleq from proof.
    /// </summary>
    /// <param name="proofs"></param>
    /// <returns>proof without dleq</returns>
    public static List<Proof> StripDleq(List<Proof> proofs)
    {
        foreach (var proof in proofs)
        {
            proof.DLEQ = null;
        }

        return proofs;
    }

    /// <summary>
    ///  Method creating proofs, from provided promises (blinded signatures)
    /// </summary>
    /// <param name="promise">Blinded Signature</param>
    /// <param name="r">Blinding factor</param>
    /// <param name="secret">Yeah, secret</param>
    /// <param name="amountPubkey">Key, corresponding to proof amount</param>
    /// <returns>Valid proof</returns>
    private static Proof ConstructProofFromPromise(
        BlindSignature promise,
        ECPrivKey r,
        DotNut.ISecret secret,
        ECPubKey amountPubkey)
    {

        //unblind signature
        var C = DotNut.Cashu.ComputeC(promise.C_, r, amountPubkey);

        if (promise.DLEQ is not null)
        {
            promise.DLEQ = new DLEQProof
            {
                E = promise.DLEQ.E,
                S = promise.DLEQ.S,
                R = r
            };
        }

        return new Proof
        {
            Id = promise.Id,
            Amount = promise.Amount,
            Secret = secret,
            C = C,
            DLEQ = promise.DLEQ,
        };
    }

    /// <summary>
    /// Create Proofs from BlindSignature array
    /// </summary>
    /// <param name="promises">Blind Signatures</param>
    /// <param name="rs">Blinding Factors</param>
    /// <param name="secrets">yeah, secrets</param>
    /// <param name="keyset"></param>
    /// <returns>Proofs Constructed with params.</returns>
    public static Proof[] CreateProofs(BlindSignature[] promises, PrivKey[] rs, DotNut.ISecret[] secrets,
        Keyset keyset)
    {
        var keysetId = promises.Select(p => p.Id).Distinct().ToList();
        //we should create that many proofs as there are signatures. when returning the fee, mint will return signatures for outputs 
        if (keysetId.Count != 1)
        {
            throw new CashuPluginException("Error while creating proofs. All promises should be the same keyset!");
        }

        if (!keyset.GetKeysetId().Equals(keysetId.Single()))
        {
            throw new CashuPluginException(
                "Error while creating proofs. Id derived from keyset different from promises!");
        }

        var proofs = new List<Proof>();
        for (int i = 0; i < promises.Length; i++)
        {
            var p = promises[i];
            var r = rs[i];
            var secret = secrets[i];

            var A = keyset[Convert.ToUInt64(p.Amount)];

            proofs.Add(ConstructProofFromPromise(p, r, secret, A));
        }

        return proofs.ToArray();
    }

    /// <summary>
    /// Helper function creating NUT-18 payment request
    /// </summary>
    /// <param name="amount">Amount</param>
    /// <param name="invoiceId">Payment id. In this scenario invoice id.</param>
    /// <param name="endpoint">POST request endpoint. for now only http post supported</param>
    /// <param name="trustedMintsUrls">list of merchants trusted mints</param>
    /// <returns>serialized payment request</returns>
    public static string CreatePaymentRequest(Money amount, string invoiceId, string endpoint,
        IEnumerable<string>? trustedMintsUrls)
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
            Transports =
            [
                new PaymentRequestTransport
                {
                    Type = "post",
                    Target = endpoint,
                }
            ]
        };
        return paymentRequest.ToString();
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
        ulong feeReserve = 0)
    {
        keysetFee = 0;
        if (proofs.Count == 0) return false;

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
        if (feeAdvanceDiff < 0) feeAdvanceDiff = 0;

        if (feeAdvanceDiff > maximumKeysetFee)
            return false;

        long lightningFeeDiff = (long)feeReserve - (config.CustomerFeeAdvance - (long)keysetFee);
        if (lightningFeeDiff < 0) lightningFeeDiff = 0;

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
        ulong feeReserve = 0)
    {
        if (proofs.Count == 0) return false;

        ulong totalAmount = proofs.Select(p => p.Amount).Sum();

        decimal maximumKeysetFee = Math.Ceiling(config.MaxKeysetFee / 100m * totalAmount);
        decimal maximumLightningFee = Math.Ceiling(config.MaxLightningFee / 100m * totalAmount);

        long feeAdvanceDiff = (long)keysetFee - config.CustomerFeeAdvance;
        if (feeAdvanceDiff < 0) feeAdvanceDiff = 0;

        if (feeAdvanceDiff > maximumKeysetFee)
            return false;

        long lightningFeeDiff = (long)feeReserve - (config.CustomerFeeAdvance - (long)keysetFee);
        if (lightningFeeDiff < 0) lightningFeeDiff = 0;

        if (lightningFeeDiff > maximumLightningFee)
            return false;

        return true;
    }
    /// <summary>
    /// Sum ulongs
    /// </summary>
    /// <param name="values"></param>
    /// <returns></returns>
    public static ulong Sum(this IEnumerable<ulong> values)
    {
        return values.Aggregate(0UL, (current, val) => current + val);
    }

    public class SimplifiedCashuToken
    {
        public string Mint { get; set; }
        public List<Proof> Proofs { get; set; }
        public string? Memo { get; set; }
        public string Unit { get; set; }

        public ulong SumProofs => Proofs?.Select(p => p.Amount).Sum() ?? 0;
    }

    public class SendResponse
    {
        public List<Proof> Keep { get; set; }

        public List<Proof> Send { get; set; }
    }

    public class OutputData
    {
        public BlindedMessage[] BlindedMessages { get; set; }
        public DotNut.ISecret[] Secrets { get; set; }
        public PrivKey[] BlindingFactors { get; set; }
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
            { "MSAT", 3 }
        };

        if (bitcoinUnits.TryGetValue(unit, out var minorUnit))
        {
            decimal adjusted = amount / (decimal)Math.Pow(10, minorUnit);
            return (adjusted, unit);
        }

        var specialMinorUnits = new Dictionary<string, int>
        {
            { "BHD", 3 }, { "BIF", 0 }, { "CLF", 4 }, { "CLP", 0 }, { "DJF", 0 }, { "GNF", 0 },
            { "IQD", 3 }, { "ISK", 0 }, { "JOD", 3 }, { "JPY", 0 }, { "KMF", 0 }, { "KRW", 0 },
            { "KWD", 3 }, { "LYD", 3 }, { "OMR", 3 }, { "PYG", 0 }, { "RWF", 0 }, { "TND", 3 },
            { "UGX", 0 }, { "UYI", 0 }, { "UYW", 4 }, { "VND", 0 }, { "VUV", 0 }, { "XAF", 0 },
            { "XOF", 0 }, { "XPF", 0 }
        };

        int fiatMinor = specialMinorUnits.ContainsKey(unit) ? specialMinorUnits[unit] : 2;
        decimal fiatAdjusted = amount / (decimal)Math.Pow(10, fiatMinor);

        return (fiatAdjusted, unit);
    }

}