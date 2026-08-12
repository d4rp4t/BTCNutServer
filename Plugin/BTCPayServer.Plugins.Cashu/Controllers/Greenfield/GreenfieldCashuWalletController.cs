using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Controllers.Greenfield.DTOs;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Services;
using BTCPayServer.Plugins.Cashu.Wallets;
using BTCPayServer.Services.Stores;
using DotNut;
using DotNut.Abstractions;
using DotNut.NBitcoin.BIP39;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Cashu.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldCashuWalletController(
    CashuDbContextFactory cashuDbContextFactory,
    StoreRepository storeRepository,
    StatefulWalletFactory walletFactory,
    MintManager mintManager,
    RestoreService restoreService,
    ILogger<GreenfieldCashuWalletController> logger
) : ControllerBase
{
    private StoreData? StoreData => HttpContext.GetStoreDataOrNull();

    [HttpPost("~/api/v1/stores/{storeId}/cashu/wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> CreateWallet(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        if (await db.CashuWalletConfig.AnyAsync(c => c.StoreId == storeId))
        {
            return this.CreateAPIError("wallet-already-exists",
                "A Cashu wallet already exists for this store. Delete it first or use the restore endpoint.");
        }
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        db.CashuWalletConfig.Add(new CashuWalletConfig
        {
            StoreId = storeId,
            WalletMnemonic = mnemonic,
            Verified = true, // no quiz needed via API
        });
        await db.SaveChangesAsync();

        return Ok(new WalletCreatedResponseDto(Mnemonic: mnemonic.ToString()));
    }

    [HttpPost("~/api/v1/stores/{storeId}/cashu/wallet/restore")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> RestoreWallet(string storeId, [FromBody] RestoreWalletRequestDto request)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        if (await db.CashuWalletConfig.AnyAsync(c => c.StoreId == storeId))
        {
            return this.CreateAPIError("wallet-already-exists",
                "A Cashu wallet already exists for this store. Delete it first.");
        }
        Mnemonic mnemonic;
        try
        {
            mnemonic = new Mnemonic(request.Mnemonic);
        }
        catch
        {
            return this.CreateAPIError("invalid-mnemonic", "The provided mnemonic is invalid.");
        }

        // the constructor only checks the word count and wordlist membership
        if (!mnemonic.IsValidChecksum)
        {
            return this.CreateAPIError("invalid-mnemonic",
                "The provided mnemonic has an invalid checksum.");
        }

        if (request.MintUrls is { Count: > 0 })
        {
            var invalidMints = request.MintUrls
                .Where(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri)
                            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                .ToList();

            if (invalidMints.Count > 0)
            {
                return this.CreateAPIError("invalid-mint-urls",
                    $"Invalid mint URLs: {string.Join(", ", invalidMints)}");
            }
            db.CashuWalletConfig.Add(new CashuWalletConfig
            {
                StoreId = storeId,
                WalletMnemonic = mnemonic,
                Verified = true,
            });
            await db.SaveChangesAsync();

            var normalizedMints = request.MintUrls.Select(MintManager.NormalizeMintUrl).ToList();
            var jobId = restoreService.QueueRestore(storeId, normalizedMints, request.Mnemonic);
            return Ok(new RestoreStartedResponseDto(JobId: jobId));
        }

        db.CashuWalletConfig.Add(new CashuWalletConfig
        {
            StoreId = storeId,
            WalletMnemonic = mnemonic,
            Verified = true,
        });
        await db.SaveChangesAsync();

        return Ok(new RestoreStartedResponseDto(JobId: null));
    }

    [HttpGet("~/api/v1/stores/{storeId}/cashu/wallet/restore/{jobId}")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetRestoreStatus(string storeId, string jobId)
    {
        var status = restoreService.GetRestoreStatus(jobId);

        if (status is null || status.StoreId != storeId)
        {
            return this.CreateAPIError(404, "restore-job-not-found",
                "The restore job was not found.");
        }
        return Ok(ToResponse(status));
    }

    [HttpGet("~/api/v1/stores/{storeId}/cashu/wallet/restore")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetRestoreStatuses(string storeId)
    {
        var statuses = restoreService.GetStoreRestoreStatuses(storeId);
        return Ok(statuses.Select(ToResponse));
    }

    [HttpDelete("~/api/v1/stores/{storeId}/cashu/wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> DeleteWallet(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        await db.CashuWalletConfig.Where(c => c.StoreId == storeId).ExecuteDeleteAsync();
        await db.Proofs.Where(p => p.StoreId == storeId).ExecuteDeleteAsync();
        await db.ExportedTokens.Where(t => t.StoreId == storeId).ExecuteDeleteAsync();
        await db.LightningPayments.Where(p => p.StoreId == storeId).ExecuteDeleteAsync();
        await db.LightningInvoices.Where(i => i.StoreId == storeId).ExecuteDeleteAsync();

        if (StoreData is not { } store)
            return this.CreateAPIError(404, "store-not-found", "Store not found.");
        var blob = store.GetStoreBlob();
        blob.SetExcluded(CashuPlugin.CashuPmid, true);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);

        return Ok();
    }


    [HttpGet("~/api/v1/stores/{storeId}/cashu/wallet/balances")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetWallet(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var storeKeysetIds = await db.Proofs
            .Where(p => p.StoreId == storeId && p.Status == ProofState.Available)
            .Select(p => p.Id)
            .Distinct()
            .ToListAsync();

        var mints = await db.MintKeys
            .Where(mk => storeKeysetIds.Contains(mk.KeysetId))
            .Select(mk => mk.Mint.Url)
            .Distinct()
            .ToListAsync();

        var balances = new List<WalletBalanceItemDto>();
        var unavailableMints = new List<string>();

        foreach (var mint in mints)
        {
            try
            {
                using var cashuHttpClient = CashuUtils.GetCashuHttpClient(mint);
                var keysets = await cashuHttpClient.GetKeysets();

                var localProofs = await db.Proofs
                    .Where(p =>
                        keysets.Keysets.Select(k => k.Id).Contains(p.Id)
                        && p.StoreId == storeId
                        && p.Status == ProofState.Available)
                    .ToListAsync();

                var grouped = localProofs
                    .Select(p => new { Proof = p, Keyset = keysets.Keysets.FirstOrDefault(k => k.Id == p.Id) })
                    .Where(x => x.Keyset != null)
                    .GroupBy(x => x.Keyset!.Unit)
                    .Select(g => new WalletBalanceItemDto(
                        Mint: mint,
                        Unit: g.Key,
                        Amount: g.Aggregate(0UL, (sum, x) => sum + x.Proof.Amount)
                    ));

                balances.AddRange(grouped);
            }
            catch (Exception ex)
            {
                logger.LogDebug("(Cashu) Could not load mint {Mint}: {Error}", mint, ex.Message);
                unavailableMints.Add(mint);
            }
        }

        return Ok(new WalletResponseDto(
            Balances: balances.OrderByDescending(b => b.Amount).ToList(),
            UnavailableMints: unavailableMints
        ));
    }


    [HttpPost("~/api/v1/stores/{storeId}/cashu/wallet/check-token-states")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> CheckTokenStates(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var unspentTokens = await db.ExportedTokens
            .Include(t => t.Proofs)
            .Where(t => t.StoreId == storeId && !t.IsUsed)
            .ToListAsync();

        if (unspentTokens.Count == 0)
        {
            return Ok(new CheckTokenStatesResponseDto(MarkedSpent: 0, FailedMints: []));
        }
        var checkTasks = unspentTokens
            .GroupBy(t => t.Mint)
            .Select(async group =>
            {
                var mint = group.Key;
                var tokens = group.ToList();
                try
                {
                    var wallet = Wallet.Create().WithMint(mint).WithKeysetSync(false);
                    var allProofs = tokens
                        .Where(t => t.Proofs is { Count: > 0 })
                        .SelectMany(t => t.Proofs!)
                        .ToList();

                    if (allProofs.Count == 0)
                    {
                        return (Mint: mint, SpentTokens: new List<ExportedToken>(), Error: (Exception?)null);
                    }
                    var states = await wallet.CheckState(allProofs);
                    var proofToSpent = allProofs
                        .Zip(states.States, (p, s) => new { p.ProofId, Spent = s.State == DotNut.ApiModels.StateResponseItem.TokenState.SPENT })
                        .ToDictionary(x => x.ProofId, x => x.Spent);

                    var spentTokens = tokens
                        .Where(t => t.Proofs is { Count: > 0 } && t.Proofs.Any(p => proofToSpent.TryGetValue(p.ProofId, out var spent) && spent))
                        .ToList();

                    return (Mint: mint, SpentTokens: spentTokens, Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Mint: mint, SpentTokens: new List<ExportedToken>(), Error: ex);
                }
            });

        var results = await Task.WhenAll(checkTasks);

        var toMarkSpent = new List<ExportedToken>();
        var failedMints = new List<string>();

        foreach (var result in results)
        {
            if (result.Error != null) { failedMints.Add(result.Mint); continue; }
            toMarkSpent.AddRange(result.SpentTokens);
        }

        foreach (var token in toMarkSpent)
        {
            token.IsUsed = true;
            if (token.Proofs != null)
            {
                foreach (var proof in token.Proofs)
                {
                    proof.Status = ProofState.Spent;
                }
            }
        }

        if (toMarkSpent.Count > 0)
        {
            await db.SaveChangesAsync();
        }

        return Ok(new CheckTokenStatesResponseDto(MarkedSpent: toMarkSpent.Count, FailedMints: failedMints));
    }

    [HttpDelete("~/api/v1/stores/{storeId}/cashu/wallet/spent-proofs")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> RemoveSpentProofs(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var proofsToCheck = await db.Proofs
            .Where(p => p.StoreId == storeId && p.Status == ProofState.Available)
            .ToListAsync();

        if (proofsToCheck.Count == 0)
        {
            return Ok(new RemoveSpentProofsResponseDto(Removed: 0, FailedMints: []));
        }
        var keysetIds = proofsToCheck.Select(p => p.Id).Distinct();
        var keysetToMintMap = await mintManager.MapKeysetIdsToMints(keysetIds);

        var checkTasks = proofsToCheck
            .Where(p => keysetToMintMap.ContainsKey(p.Id.ToString()))
            .GroupBy(p => keysetToMintMap[p.Id.ToString()])
            .Select(async group =>
            {
                var (mintUrl, _) = group.Key;
                var proofs = group.ToList();
                try
                {
                    var wallet = Wallet.Create().WithMint(mintUrl).WithKeysetSync(false);
                    var states = await wallet.CheckState(proofs.Select(p => p.ToDotNutProof()).ToList());
                    var spent = proofs
                        .Zip(states.States, (p, s) => (Item: p, Spent: s.State == DotNut.ApiModels.StateResponseItem.TokenState.SPENT))
                        .Where(x => x.Spent)
                        .Select(x => x.Item)
                        .ToList();
                    return (Mint: mintUrl, SpentProofs: spent, Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Mint: mintUrl, SpentProofs: new List<StoredProof>(), Error: ex);
                }
            });

        var results = await Task.WhenAll(checkTasks);

        var toRemove = new List<StoredProof>();
        var failedMints = new List<string>();

        foreach (var result in results)
        {
            if (result.Error != null) { failedMints.Add(result.Mint); continue; }
            toRemove.AddRange(result.SpentProofs);
        }

        if (toRemove.Count > 0)
        {
            db.Proofs.RemoveRange(toRemove);
            await db.SaveChangesAsync();
        }

        return Ok(new RemoveSpentProofsResponseDto(Removed: toRemove.Count, FailedMints: failedMints));
    }

    [HttpPost("~/api/v1/stores/{storeId}/cashu/wallet/export")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ExportBalance(string storeId, [FromBody] ExportBalanceRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.MintUrl) || string.IsNullOrWhiteSpace(request.Unit))
        {
            return this.CreateAPIError("invalid-request", "MintUrl and Unit are required.");
        }

        await using var db = cashuDbContextFactory.CreateContext();

        List<DotNut.ApiModels.GetKeysetsResponse.KeysetItemResponse> keysets;
        try
        {
            var wallet = await walletFactory.CreateAsync(storeId, request.MintUrl, request.Unit);
            keysets = await wallet.GetKeysets();
            if (keysets == null || keysets.Count == 0)
            {
                throw new Exception("No keysets found.");
            }
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("mint-unavailable", $"Could not reach mint: {ex.Message}");
        }

        var selectedProofs = await db.Proofs
            .Where(p =>
                p.StoreId == storeId
                && keysets.Select(k => k.Id).Contains(p.Id)
                && p.Status == ProofState.Available)
            .ToListAsync();

        if (selectedProofs.Count == 0)
        {
            return this.CreateAPIError("no-proofs",
                "No available proofs for the specified mint and unit.");
        }
        var cashuToken = new CashuToken
        {
            Tokens =
            [
                new CashuToken.Token
                {
                    Mint = request.MintUrl,
                    Proofs = selectedProofs.Select(p => p.ToDotNutProof()).ToList(),
                },
            ],
            Memo = "Cashu Token withdrawn from BTCNutServer",
            Unit = request.Unit,
        };

        var tokenAmount = selectedProofs.Aggregate(0UL, (sum, p) => sum + p.Amount);
        var serializedToken = cashuToken.Encode();

        foreach (var proof in selectedProofs)
            proof.Status = ProofState.Exported;

        var exportedToken = new ExportedToken
        {
            SerializedToken = serializedToken,
            Amount = tokenAmount,
            Unit = request.Unit,
            Mint = request.MintUrl,
            StoreId = storeId,
            IsUsed = false,
            Proofs = selectedProofs,
        };

        ExportedTokenResponseDto? response = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                db.ExportedTokens.Add(exportedToken);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                response = ToExportedTokenResponseDto(exportedToken);
            }
            catch
            {
                await tx.RollbackAsync();
            }
        });

        if (response is null)
        {
            return this.CreateAPIError("export-failed", "Failed to export token.");
        }

        return Ok(response);
    }

    internal static ExportedTokenResponseDto ToExportedTokenResponseDto(ExportedToken t) => new(
        Id: t.Id,
        Amount: t.Amount,
        Unit: t.Unit,
        Mint: t.Mint,
        Token: t.SerializedToken,
        IsUsed: t.IsUsed,
        CreatedAt: t.CreatedAt
    );

    private static RestoreStatusResponseDto ToResponse(RestoreStatus s) => new(
        JobId: s.JobId,
        Status: s.Status.ToString(),
        TotalMints: s.TotalMints,
        ProcessedMints: s.ProcessedMints,
        UnreachableMints: s.UnreachableMints,
        Errors: s.Errors,
        QueuedAt: s.QueuedAt,
        StartedAt: s.StartedAt,
        CompletedAt: s.CompletedAt,
        RestoredMints: s.RestoredMints?.Select(m => new RestoredMintResponseDto(
            MintUrl: m.MintUrl,
            Balances: m.Balances
        )).ToList() ?? []
    );
}
