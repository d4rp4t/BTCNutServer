#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Errors;
using DotNut;
using DotNut.Api;
using DotNut.ApiModels;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Cashu.CashuAbstractions;

/// <summary>
/// Class leveraging cashu wallet functionalities.
/// </summary>
public class CashuWallet
{
    private readonly CashuHttpClient _cashuHttpClient;
    private readonly ILightningClient? _lightningClient;
    private Keyset? _keys;
    private readonly string _mintUrl;
    private List<GetKeysetsResponse.KeysetItemResponse>? _keysets;
    private readonly string _unit;
    private readonly CashuDbContextFactory? _dbContextFactory;
    public bool HasLightningClient => _lightningClient is not null;
    public CashuWallet(ILightningClient lightningClient, string mint, string unit = "sat", CashuDbContextFactory? cashuDbContextFactory = null)
    {
        _lightningClient = lightningClient;
        _mintUrl = mint;
        _cashuHttpClient = CashuUtils.GetCashuHttpClient(mint);
        _unit = unit;
        _dbContextFactory = cashuDbContextFactory;
    }
    
    //In case of just swapping token and saving in db, store doesn't have to have lighting client configured
    public CashuWallet(string mint, string unit = "sat", CashuDbContextFactory? cashuDbContextFactory = null)
    {
        _cashuHttpClient = CashuUtils.GetCashuHttpClient(mint);
        _mintUrl = mint;
        _unit = unit;
        _dbContextFactory = cashuDbContextFactory;
    }

    /// <summary>
    /// Method creating maximal amount melt quote for provided Token. Doesn't verify the single unit price.
    /// </summary>
    /// <param name="token">Cashu decrypted token</param>
    /// <param name="singleUnitPrice">Price per unit of token</param>
    /// <param name="keysets"></param>
    /// <returns>Melt Quote that has to be sent to mint</returns>
    public async Task<CreateMeltQuoteResult> CreateMeltQuote(CashuUtils.SimplifiedCashuToken token, decimal singleUnitPrice, List<GetKeysetsResponse.KeysetItemResponse> keysets)
    {
        try
        {
            if (_lightningClient == null)
            {
                throw new CashuPluginException("Lightning client is not configured");
            }

            var tokenWorth = Math.Floor(token.SumProofs * singleUnitPrice);

            var initialInvoice = await _lightningClient.CreateInvoice(
                LightMoney.Satoshis(tokenWorth),
                "initial invoice for melt quote",
                new TimeSpan(0, 0, 30, 0)
            );

            //check the fee reserve for this melt
            var initialMeltQuote =
                await _cashuHttpClient.CreateMeltQuote<PostMeltQuoteBolt11Response, PostMeltQuoteBolt11Request>(
                    "bolt11",
                    new PostMeltQuoteBolt11Request()
                    {
                        Request = initialInvoice.BOLT11,
                        Unit = token.Unit ?? "sat"
                    }
                );
            //calculate the keyset fee
            var keysetFee = token.Proofs.ComputeFee(
                keysets.ToDictionary(k => k.Id, k => k.InputFee ?? 0)
            );

            //subtract fee reserve and keysetFee from Proofs.
            var amountWithoutFees = singleUnitPrice * (
                initialMeltQuote.Amount -
                (ulong)initialMeltQuote.FeeReserve -
                keysetFee
            );

            var invoiceWithFeesSubtracted = await _lightningClient.CreateInvoice(
                new CreateInvoiceParams(
                    LightMoney.Satoshis(amountWithoutFees),
                    "Cashu token melt in BTCPay Cashu Plugin",
                    new TimeSpan(0, 2, 0, 0)
                )
            );

            var meltQuote = await _cashuHttpClient.CreateMeltQuote<PostMeltQuoteBolt11Response, PostMeltQuoteBolt11Request>(
                    "bolt11",
                    new PostMeltQuoteBolt11Request
                    {
                        Request = invoiceWithFeesSubtracted.BOLT11,
                        Unit = token.Unit ?? "sat"
                    }
                );
            return new CreateMeltQuoteResult
            {
                Invoice = invoiceWithFeesSubtracted,
                MeltQuote = meltQuote,
                KeysetFee = keysetFee
            };
        }
        catch(Exception ex)
        {
            return new CreateMeltQuoteResult
            {
                Error = ex,
            };
        }
    }

    /// <summary>
    /// Melt your proofs and get change
    /// </summary>
    /// <param name="meltQuote">melt Quote that mint has to pay</param>
    /// <param name="proofsToMelt">proofs, with amount AT LEAST corresponding to amount + fee reserve + keyset fee</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Change proofs</returns>
    public async Task<MeltResult> Melt(PostMeltQuoteBolt11Response meltQuote, List<Proof> proofsToMelt, CancellationToken cancellationToken = default)
    {
        if (_lightningClient == null)
        {
            throw new CashuPluginException("Lightning client is not configured");
        }
        
        var activeKeyset = await GetActiveKeyset();
        var keys = await GetKeys(activeKeyset.Id);
        if (keys == null || !keys.Any())
        {
            throw new CashuPluginException("No keyset available");
        }
        
        var blankOutputs = CashuUtils.CreateBlankOutputs((ulong)meltQuote.FeeReserve, activeKeyset.Id, keys);
        
        var request = new PostMeltBolt11Request
        {
           Quote = meltQuote.Quote,
           Inputs = proofsToMelt.ToArray(),
           Outputs =  blankOutputs.BlindedMessages
        };

        try
        {
            var response = await _cashuHttpClient.Melt<PostMeltQuoteBolt11Response, PostMeltBolt11Request>(
                "bolt11",
                request, cancellationToken);
            
            Proof[]? change = null;
            
            if (response?.Change != null && response.Change.Length != 0 && blankOutputs.BlindingFactors.Length >= response.Change.Length )
            {
                change = CashuUtils.CreateProofs(response.Change, blankOutputs.BlindingFactors,
                    blankOutputs.Secrets, keys);
            }
            return new MeltResult()
            {
                BlankOutputs = blankOutputs,
                ChangeProofs = change,
                Quote = response
            };
        }
        catch (Exception e)
        {
            return new MeltResult()
            {
                BlankOutputs = blankOutputs,
                ChangeProofs = null,
                Error = e,
                Quote = meltQuote
            };
        }
    }

    /// <summary>
    /// Swap proofs to receive proofs and prevent double spend.
    /// </summary>
    /// <param name="proofsToReceive">proofs that we want to swap</param>
    /// <param name="inputFee">input_fee_ppk</param>
    /// <returns></returns>
    public async Task<SwapResult> Receive(List<Proof> proofsToReceive, ulong inputFee = 0)
    {
        var keyset = await GetActiveKeyset();
        var keys = await this.GetKeys(keyset.Id);

        var amounts = proofsToReceive.Select(proof => proof.Amount).ToList();
        
        if (inputFee == 0)
        {
            return await Swap(proofsToReceive, amounts, keyset.Id, keys);
        }
        
        var inputAmount = amounts.Sum();
        if (inputAmount <= inputFee)
        {
            throw new CashuPluginException("Input fee bigger than swap amount.");
        }

        var totalAmount = inputAmount - inputFee;
        
        amounts = CashuUtils.SplitToProofsAmounts(totalAmount, keys);
        return await Swap(proofsToReceive, amounts, keyset.Id, keys);
    }

    /// <summary>
    /// Swaps token in order to rotate secrets (prevent double spending) and/or change proofs amounts. Input Fee not included!!!
    /// </summary>
    /// <param name="proofsToSwap">Proofs that we want swapped</param>
    /// <param name="amounts">amounts of these proofs we want to receive</param>
    /// <param name="keysetId"></param>
    /// <param name="keys"></param>
    /// <exception cref="CashuPaymentException"></exception>
    /// 
    /// <returns>Freshly minted proofs</returns>
    public async Task<SwapResult> Swap(List<Proof> proofsToSwap, List<ulong> amounts, KeysetId? keysetId = null, Keyset? keys = null)
    {
        keysetId??= (await GetActiveKeyset()).Id;
        keys??= await GetKeys(keysetId);
        
        var outputs = CashuUtils.CreateOutputs(amounts, keysetId, keys);
        
        var request = new PostSwapRequest()
        {
            Inputs = proofsToSwap.ToArray(),
            Outputs = outputs.BlindedMessages
        };
        try
        {
            var response = await _cashuHttpClient.Swap(request);
            return new SwapResult
            {
                ProvidedOutputs = outputs,
                ResultProofs =
                    CashuUtils.CreateProofs(response.Signatures, outputs.BlindingFactors, outputs.Secrets, keys),
            };
        }
        catch (Exception e)
        {
            return new SwapResult
            {
                ProvidedOutputs = outputs,
                Error = e,
            };
        }
        
    }
    
    /// <summary>
    /// Returns mint's keys for provided ID. If not specified returns first active keyset for sat unit
    /// </summary>
    /// <param name="keysetId"></param>
    /// <param name="forceRefresh"></param>
    /// <returns></returns>
    public async Task<Keyset?> GetKeys(KeysetId? keysetId, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            
        }
        //if there's no keysetsIds in wallet - get them
        if (_keysets == null)
        {
            await this.GetKeysets();
        }
        
        // if no keysetId specified - choose active one
        if (keysetId == null)
        {
            var localKeyset = await this.GetActiveKeyset();
            keysetId = new KeysetId(localKeyset.Id.ToString());
        }

        //try to fetch keys from database
        _keys = await TryLoadKeysetFromDb(keysetId);
        
        //if there are keys for this keyset - return them
        if (_keys != null && _keys.GetKeysetId() == keysetId)
        {
            return _keys;
        }
        
        var keys =  await _cashuHttpClient.GetKeys(keysetId);
        
        if (keys.Keysets == null||keys.Keysets.Length == 0)
        {
            throw new CashuPluginException("Couldn't fetch keys for provided keyset");
        }
        
        //When we fetch keyset for given ID, it should be always returned as single element.
        _keys = keys.Keysets.FirstOrDefault()?.Keys;

        if (_keys!=null)
        {
            await SaveKeysetToDb(keysetId, _keys);
        }
        
        return _keys;
    }

    /// <summary>
    /// Returns mints keysets for all units.
    /// </summary>
    /// <returns></returns>
    public async Task<List<GetKeysetsResponse.KeysetItemResponse>> GetKeysets()
    {
        // Always fetch the current keysets from the mint
        var getKeysetResponse = await _cashuHttpClient.GetKeysets();
        var keysets = getKeysetResponse.Keysets.Where(k=>k.Id.ToString().Length==16).ToList();
        _keysets = keysets;
        
        // If we have a database context, check for missing keysets and fetch their keys
        if (_dbContextFactory == null)
        {
            return _keysets;
        }
        await using var db = _dbContextFactory.CreateContext();
        var missingKeysetIds = await GetMissingKeysetIds();
        if (missingKeysetIds == null)
        {
            return _keysets;
        }
        foreach (var keysetId in missingKeysetIds)
        {
            // Fetch keys for this keyset and store them
            await GetKeys(keysetId, true);
        }
        return _keysets;
    }
    
    
    /// <summary>
    /// Returns active keyset for current unit
    /// </summary>
    /// <returns></returns>
    public async Task<GetKeysetsResponse.KeysetItemResponse> GetActiveKeyset()
    {
        if (this._keysets == null)
        {
            await this.GetKeysets();
        }
        if (this._keysets == null)
        {
            throw new CashuPluginException("Couldn't get keysets!");
        }
        var filteredKeysets = _keysets.Where(
            keyset => keyset.Active && keyset.Id.ToString().StartsWith("00") && keyset.Unit == _unit);
        var activeKeyset = filteredKeysets.OrderBy(keyset => keyset.InputFee).FirstOrDefault();
        if (activeKeyset == null)
        {
            throw new CashuPluginException("Could not find active keyset for this unit!");
        }
        return activeKeyset;
    }
    /// <summary>
    /// Check if mint exists in database. If not, create it. It basically allows you to tie keys to this mint in db.
    /// </summary>
    /// <param name="db">database context. in this case - CashuDbContext instance</param>
    /// <returns>Mint object</returns>
    private async Task<Mint> GetOrCreateMintInDb(CashuDbContext db)
    {
        var mint = await db.Mints.FirstOrDefaultAsync(m => m.Url == _mintUrl);
        
        if (mint == null)
        {
            mint = new Mint(_mintUrl);
            db.Mints.Add(mint);
            await db.SaveChangesAsync();
        }
        return mint;
    }
    
    /// <summary>
    /// Method saving the keyset to database. Since keys won't change for given keysetID (it's derived) it can help optimize API calls to the mint.
    /// </summary>
    /// <param name="keysetId"></param>
    /// <param name="keyset"></param>
    private async Task SaveKeysetToDb(KeysetId keysetId, Keyset keyset)
    {
        if (_dbContextFactory == null) return;
        
        await using var db = _dbContextFactory.CreateContext(); 
            
        var mint = await GetOrCreateMintInDb(db);
        
        var existingEntry = await db.MintKeys.FirstOrDefaultAsync(mk => 
            mk.MintId == mint.Id && mk.KeysetId == keysetId);
        
        if(existingEntry is null)       
        {
            db.MintKeys.Add(new MintKeys
            {
                MintId = mint.Id,
                Mint = mint,
                KeysetId = keysetId,
                Unit = _unit,
                Keyset = keyset
            });
        }
        
        await db.SaveChangesAsync();
    }
    
    /// <summary>
    /// If there's no matching mint in database, add new entry
    /// </summary>
    /// <param name="keysetId"></param>
    /// <returns></returns>
    private async Task<Keyset?> TryLoadKeysetFromDb(KeysetId keysetId)
    {
        if (_dbContextFactory == null) return null;
        
        await using var db = _dbContextFactory.CreateContext();
        var mint = await GetOrCreateMintInDb(db);
        
        var entry = await db.MintKeys.FirstOrDefaultAsync(mk => 
            mk.MintId == mint.Id && mk.KeysetId == keysetId);
            
        return entry?.Keyset;
    }
    
    /// <summary>
    /// Function getting keysets that aren't stored in database. Since keysetId is derived from keyset, it won't be changed, so it can be stored against it.
    /// </summary>
    /// <returns>List of KeysetItemResponse</returns>
    private async Task<List<KeysetId>?> GetMissingKeysetIds()
    {
        if (_dbContextFactory == null)
        {
            return _keysets?.Select(k => k.Id).ToList();
        }
        await using var db = _dbContextFactory.CreateContext();
        var mint = await GetOrCreateMintInDb(db);
        var dbKeysets = await db.Set<MintKeys>()
            .Where(mk => mk.MintId == mint.Id)
            .Select(mk => mk.KeysetId.ToString())
            .ToListAsync();
        
        // Find keysets that are not in the database
        return _keysets?.Where(k => !dbKeysets.Contains(k.Id.ToString()))
            .Select(k => new KeysetId(k.Id.ToString()))
            .ToList();
    }

    public async Task<StateResponseItem.TokenState> CheckTokenState(List<Proof> proofs)
    {
        var yBytes = proofs.Select(p => p.Secret.ToCurve().ToBytes());
        var ysStrings = yBytes.Select(y=>Convert.ToHexString(y).ToLower()).ToArray();
        
        var request = new PostCheckStateRequest
        { 
            Ys = ysStrings
        };
        var response = await _cashuHttpClient.CheckState(request);

        if (response.States.Any(r => r.State == StateResponseItem.TokenState.SPENT))
            return StateResponseItem.TokenState.SPENT; 

        if (response.States.Any(r => r.State == StateResponseItem.TokenState.PENDING))
            return StateResponseItem.TokenState.PENDING; 
        
        return StateResponseItem.TokenState.UNSPENT;
    }
    public async Task<StateResponseItem.TokenState> CheckTokenState(List<StoredProof> proofs)
    {
        var dotnutProofs = proofs.Select(p => p.ToDotNutProof()).ToList();
        return await CheckTokenState(dotnutProofs);
    }

    public async Task<PostRestoreResponse> RestoreProofsFromInputs(BlindedMessage[] blindedMessages, CancellationToken cts = default)
    {
        var payload = new PostRestoreRequest { Outputs = blindedMessages };
        var response = await this._cashuHttpClient.Restore(payload, cts);
        return response;
    }

    public async Task<bool> ValidateLightningInvoicePaid(string? invoiceId)
    {
        if (invoiceId == null)
        {
            throw new CashuPluginException("Invalid lightning invoice id");
        }
        if (_lightningClient is null)
        {
            throw new CashuPluginException("Lightning Client has not been configured.");
        }

        var invoice = await _lightningClient.GetInvoice(invoiceId);

        return invoice?.Status == LightningInvoiceStatus.Paid;
    }

    public async Task<PostMeltQuoteBolt11Response> CheckMeltQuoteState(string meltQuoteId, CancellationToken cts = default)
    {
        return await this._cashuHttpClient.CheckMeltQuote<PostMeltQuoteBolt11Response>("bolt11", meltQuoteId, cts);

    }
    
}


public class MeltResult
{
    public bool Success => Error == null && Quote != null;
    public PostMeltQuoteBolt11Response? Quote { get; set; }
    public Proof[]? ChangeProofs { get; set; }
    public required CashuUtils.OutputData BlankOutputs { get; set; }
    public Exception? Error { get; set; }
}

public class SwapResult
{
    public bool Success => Error == null && ResultProofs != null;
    public required CashuUtils.OutputData ProvidedOutputs { get; set; }
    public Proof[]? ResultProofs { get; set; }
    public Exception? Error { get; set; }
}

public class CreateMeltQuoteResult
{
    public bool Success => Error == null && MeltQuote != null && Invoice != null && KeysetFee != null;
    public PostMeltQuoteBolt11Response? MeltQuote { get; set; }
    public LightningInvoice? Invoice { get; set; }
    public ulong? KeysetFee { get; set; }
    public Exception? Error { get; set; }
}