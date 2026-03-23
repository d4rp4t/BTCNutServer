using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Errors;
using BTCPayServer.Plugins.Cashu.Services;
using DotNut;
using DotNut.Abstractions;
using DotNut.Abstractions.Handlers;
using DotNut.ApiModels;
using Microsoft.EntityFrameworkCore;
using DotNutOutputData = DotNut.Abstractions.OutputData;
using Utils = DotNut.Abstractions.Utils;

namespace BTCPayServer.Plugins.Cashu.CashuAbstractions;

/// <summary>
/// Class leveraging cashu wallet functionalities.
/// </summary>
public class StatefulWallet : IDisposable
{
    private readonly ILightningClient? _lightningClient;
    private readonly string _mintUrl;
    private readonly string _unit;
    private readonly CashuDbContextFactory? _dbContextFactory;
    private readonly MintManager? _mintManager;
    private readonly string? _storeId;
    public bool HasLightningClient => _lightningClient is not null;

    private readonly Wallet _wallet;
    private bool _initialized;

    public StatefulWallet(
        ILightningClient lightningClient,
        string mint,
        string unit = "sat",
        CashuDbContextFactory? cashuDbContextFactory = null,
        MintManager? mintManager = null,
        string? storeId = null
    )
    {
        _lightningClient = lightningClient;
        _mintUrl = mint;
        _unit = unit;
        _dbContextFactory = cashuDbContextFactory;
        _mintManager = mintManager;
        _storeId = storeId;

        _wallet = (Wallet)Wallet.Create().WithMint(CashuUtils.GetCashuHttpClient(mint), true);
    }

    //In case of just swapping token and saving in db, store doesn't have to have lighting client configured
    public StatefulWallet(
        string mint,
        string unit = "sat",
        CashuDbContextFactory? cashuDbContextFactory = null,
        MintManager? mintManager = null,
        string? storeId = null
    )
    {
        _mintUrl = mint;
        _unit = unit;
        _dbContextFactory = cashuDbContextFactory;
        _mintManager = mintManager;
        _storeId = storeId;

        _wallet = (Wallet)Wallet.Create().WithMint(CashuUtils.GetCashuHttpClient(mint), true);
    }

    private async Task EnsureInitialized()
    {
        if (_initialized)
            return;

        if (_dbContextFactory != null)
        {
            await using var db = _dbContextFactory.CreateContext();
            var mint = await db.Mints.FirstOrDefaultAsync(m => m.Url == _mintUrl);
            if (mint != null)
            {
                var keysets = await db
                    .MintKeys.Where(mk => mk.MintId == mint.Id)
                    .Select(mk => mk.Keyset)
                    .ToListAsync();

                var keysList = keysets
                    .Select(k => new GetKeysResponse.KeysetItemResponse
                    {
                        Id = k.GetKeysetId(),
                        Unit = _unit,
                        Keys = k,
                    })
                    .ToList();

                _wallet.WithKeys(keysList);
            }

            if (_storeId != null)
            {
                var walletConfig = await db.CashuWalletConfig.FirstOrDefaultAsync(c =>
                    c.StoreId == _storeId
                );
                if (walletConfig != null && walletConfig.WalletMnemonic != null)
                {
                    _wallet.WithMnemonic(walletConfig.WalletMnemonic);
                    _wallet.WithCounter(new DbCounter(_dbContextFactory, _storeId));
                }
            }
        }

        _initialized = true;
    }

    /// <summary>
    /// Method creating maximal amount melt quote for provided Token. Doesn't verify the single unit price.
    /// </summary>
    /// <param name="token">Cashu decrypted token</param>
    /// <param name="singleUnitPrice">Price per unit of token</param>
    /// <param name="keysets"></param>
    /// <returns>Melt Quote that has to be sent to mint</returns>
    public async Task<CreateMeltQuoteResult> CreateMaxMeltQuote(
        CashuOperationContext opCtx,
        List<GetKeysetsResponse.KeysetItemResponse> keysets
    )
    {
        try
        {
            await EnsureInitialized();

            if (_lightningClient == null)
            {
                throw new CashuPluginException("Lightning client is not configured");
            }


            var initialInvoice = await _lightningClient.CreateInvoice(
                opCtx.Value,
                "initial invoice for melt quote",
                new TimeSpan(0, 0, 30, 0)
            );

            //check the fee reserve for this melt
            var unit = opCtx.Token.Unit ?? "sat";
            var initialMeltHandler = await _wallet
                .CreateMeltQuote()
                .WithUnit(unit)
                .WithInvoice(initialInvoice.BOLT11)
                .ProcessAsyncBolt11();

            var initialMeltQuote = initialMeltHandler.GetQuote();

            //calculate the keyset fee
            var keysetFee = opCtx.Token.Proofs.ComputeFee(
                keysets.ToDictionary(k => k.Id, k => k.InputFee ?? 0)
            );

            //subtract fee reserve and keysetFee from Proofs.
            var amountWithoutFees =
                opCtx.UnitValue
                * (initialMeltQuote.Amount - (ulong)initialMeltQuote.FeeReserve - keysetFee);

            var invoiceWithFeesSubtracted = await _lightningClient.CreateInvoice(
                new CreateInvoiceParams(
                    amountWithoutFees,
                    "Cashu token melt in BTCPay Cashu Plugin",
                    new TimeSpan(0, 2, 0, 0)
                )
            );

            var finalMeltHandler = await _wallet
                .CreateMeltQuote()
                .WithUnit(unit)
                .WithInvoice(invoiceWithFeesSubtracted.BOLT11)
                .ProcessAsyncBolt11();

            var meltQuote = finalMeltHandler.GetQuote();

            return new CreateMeltQuoteResult
            {
                Invoice = invoiceWithFeesSubtracted,
                MeltQuote = meltQuote,
                KeysetFee = keysetFee,
            };
        }
        catch (Exception ex)
        {
            return new CreateMeltQuoteResult { Error = ex };
        }
    }

    /// <summary>
    /// Melt your proofs and get change
    /// </summary>
    /// <param name="meltQuote">melt Quote that mint has to pay</param>
    /// <param name="proofsToMelt">proofs, with amount AT LEAST corresponding to amount + fee reserve + keyset fee</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Change proofs</returns>
    public async Task<MeltResult> Melt(
        PostMeltQuoteBolt11Response meltQuote,
        List<Proof> proofsToMelt,
        CancellationToken cancellationToken = default
    )
    {
        List<DotNutOutputData> blankOutputs = new();
        Proof[]? changeProofs = null;
        try
        {
            await EnsureInitialized();

            if (_lightningClient == null)
            {
                throw new CashuPluginException("Lightning client is not configured");
            }

            // generate blank outputs manually so we can return them in MeltResult
            var feeReserve = (ulong)meltQuote.FeeReserve;
            var activeKeysetId = await _wallet.GetActiveKeysetId(_unit, cancellationToken);
            if (activeKeysetId == null)
            {
                throw new CashuPluginException("No active keyset found for unit " + _unit);
            }

            var keys = await GetKeys(activeKeysetId); // ensures loaded/saved
            if (keys == null || !keys.Any())
            {
                throw new CashuPluginException("No keyset available");
            }

            // create blank outputs again and use them
            blankOutputs = await _wallet.CreateOutputs(
                Enumerable.Repeat(1UL, Utils.CalculateNumberOfBlankOutputs(feeReserve)).ToList(),
                activeKeysetId,
                cancellationToken
            );

            var handler = new MeltHandlerBolt11(_wallet, meltQuote, blankOutputs);

            changeProofs = (await handler.Melt(proofsToMelt, cancellationToken)).ToArray();

            // save any new keys that might have been fetched during melt
            var changeKeysetIds = changeProofs.Select(p => p.Id).Distinct();
            foreach (var kid in changeKeysetIds)
            {
                var k = await _wallet.GetKeys(kid, false, false, cancellationToken);
                if (k != null)
                    await SaveKeysetToDb(kid, k.Keys);
            }

            await SaveProofs(changeProofs);

            return new MeltResult()
            {
                BlankOutputs = blankOutputs,
                ChangeProofs = changeProofs,
                Quote = meltQuote,
            };
        }
        catch (Exception e)
        {
            return new MeltResult()
            {
                BlankOutputs = blankOutputs,
                ChangeProofs = changeProofs, // return proofs if we have them (db failure scenario)
                Error = e,
                Quote = meltQuote,
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
        await EnsureInitialized();
        var keysetId = await _wallet.GetActiveKeysetId(_unit);
        var keys = await GetKeys(keysetId);

        var amounts = proofsToReceive.Select(proof => proof.Amount).ToList();

        if (inputFee == 0)
        {
            return await Swap(proofsToReceive, amounts, keysetId, keys);
        }

        var inputAmount = amounts.Sum();
        if (inputAmount <= inputFee)
        {
            throw new CashuPluginException("Input fee bigger than swap amount.");
        }

        var totalAmount = inputAmount - inputFee;

        amounts = Utils.SplitToProofsAmounts(totalAmount, keys!);
        return await Swap(proofsToReceive, amounts, keysetId, keys);
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
    public async Task<SwapResult> Swap(
        List<Proof> proofsToSwap,
        List<ulong> amounts,
        KeysetId? keysetId = null,
        Keyset? keys = null,
        CancellationToken ct = default
    )
    {
        await EnsureInitialized();
        keysetId ??= await _wallet.GetActiveKeysetId(_unit, ct);

        var outputs = await _wallet.CreateOutputs(amounts, keysetId, ct);

        Proof[]? resultProofs = null;
        try
        {
            resultProofs = (
                await _wallet
                    .Swap()
                    .FromInputs(proofsToSwap)
                    .ForOutputs(outputs)
                    .WithDLEQVerification(true)
                    .ProcessAsync(ct)
            ).ToArray();

            await SaveProofs(resultProofs);

            return new SwapResult { ProvidedOutputs = outputs, ResultProofs = resultProofs };
        }
        catch (Exception e)
        {
            return new SwapResult
            {
                ProvidedOutputs = outputs,
                ResultProofs = resultProofs, // return proofs if network succeeded but something else failed
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
        await EnsureInitialized();

        // if no keysetId specified - choose active one
        keysetId ??= await _wallet.GetActiveKeysetId(_unit);

        var keysetResponse = await _wallet.GetKeys(keysetId, true, forceRefresh);

        if (keysetResponse?.Keys != null)
        {
            await SaveKeysetToDb(keysetId, keysetResponse.Keys);
            return keysetResponse.Keys;
        }

        return null;
    }

    /// <summary>
    /// Returns mints keysets for all units.
    /// </summary>
    /// <returns></returns>
    public async Task<List<GetKeysetsResponse.KeysetItemResponse>> GetKeysets()
    {
        await EnsureInitialized();
        var keysets = await _wallet.GetKeysets();

        if (_dbContextFactory != null && _mintManager != null)
        {
            // Check missing in DB
            var mint = await _mintManager.GetOrCreateMint(_mintUrl);
            
            await using var db = _dbContextFactory.CreateContext();
            var dbKeysets = await db
                .MintKeys.Where(mk => mk.MintId == mint.Id)
                .Select(mk => mk.KeysetId.ToString())
                .ToListAsync();

            var missing = keysets
                .Where(k => !dbKeysets.Contains(k.Id.ToString()) && k.Active)
                .ToList();

            foreach (var m in missing)
            {
                // Fetch and save
                await GetKeys(m.Id);
            }
        }

        return keysets;
    }

    /// <summary>
    /// Check if mint exists in database. If not, create it. It basically allows you to tie keys to this mint in db.
    /// </summary>
    /// <param name="db">database context. in this case - CashuDbContext instance</param>
    /// <returns>Mint object</returns>
    /// <summary>
    /// Method saving the keyset to database. Since keys won't change for given keysetID (it's derived) it can help optimize API calls to the mint.
    /// </summary>
    /// <param name="keysetId"></param>
    /// <param name="keyset"></param>
    private async Task SaveKeysetToDb(KeysetId keysetId, Keyset keyset)
    {
        if (_mintManager == null)
            return;

        await _mintManager.SaveKeyset(_mintUrl, keysetId, keyset, _unit);
    }

    private async Task SaveProofs(IEnumerable<Proof> proofs)
    {
        if (_dbContextFactory == null || _storeId == null)
            return;

        if (_mintManager != null)
        {
            await _mintManager.GetOrCreateMint(_mintUrl);
        }

        await using var db = _dbContextFactory.CreateContext();
        var dbProofs = StoredProof.FromBatch(proofs, _storeId, ProofState.Available);
        db.Proofs.AddRange(dbProofs);

        await db.SaveChangesAsync();
    }

    public async Task<StateResponseItem.TokenState> CheckTokenState(List<Proof> proofs)
    {
        await EnsureInitialized();
        // DotNut Wallet has CheckState taking proofs
        var response = await _wallet.CheckState(proofs);

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

    public async Task<List<StateResponseItem>> CheckIndividualProofStates(List<StoredProof> proofs)
    {
        await EnsureInitialized();
        var dotnutProofs = proofs.Select(p => p.ToDotNutProof()).ToList();
        var response = await _wallet.CheckState(dotnutProofs);
        return response.States.ToList();
    }

    public async Task<PostRestoreResponse> RestoreProofsFromInputs(
        BlindedMessage[] blindedMessages,
        CancellationToken cts = default
    )
    {
        await EnsureInitialized();

        var api = await _wallet.GetMintApi(cts);
        var payload = new PostRestoreRequest { Outputs = blindedMessages };
        return await api.Restore(payload, cts);
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

    public async Task<PostMeltQuoteBolt11Response> CheckMeltQuoteState(
        string meltQuoteId,
        CancellationToken cts = default
    )
    {
        await EnsureInitialized();
        var api = await _wallet.GetMintApi(cts);
        return await api.CheckMeltQuote<PostMeltQuoteBolt11Response>("bolt11", meltQuoteId, cts);
    }

    public void Dispose()
    {
        _wallet.Dispose();
    }
}

public class MeltResult
{
    public bool Success => Error == null && Quote != null;
    public PostMeltQuoteBolt11Response? Quote { get; set; }
    public Proof[]? ChangeProofs { get; set; }
    public required List<OutputData> BlankOutputs { get; set; }
    public Exception? Error { get; set; }
}

public class SwapResult
{
    public bool Success => Error == null && ResultProofs != null;
    public required List<OutputData> ProvidedOutputs { get; set; }
    public Proof[]? ResultProofs { get; set; }
    public Exception? Error { get; set; }
}

public class CreateMeltQuoteResult
{
    public bool Success =>
        Error == null && MeltQuote != null && Invoice != null && KeysetFee != null;
    public PostMeltQuoteBolt11Response? MeltQuote { get; set; }
    public LightningInvoice? Invoice { get; set; }
    public ulong? KeysetFee { get; set; }
    public Exception? Error { get; set; }
}
