using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.PaymentHandlers;
using BTCPayServer.Plugins.Cashu.Services;
using BTCPayServer.Plugins.Cashu.ViewModels;
using BTCPayServer.Services.Invoices;
using DotNut;
using DotNut.Abstractions;
using DotNut.ApiModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Cashu.Controllers;

[Route("stores")]
[Authorize(
    Policy = Policies.CanModifyStoreSettings,
    AuthenticationSchemes = AuthenticationSchemes.Cookie
)]
public class UICashuWalletController(
    InvoiceRepository invoiceRepository,
    PaymentMethodHandlerDictionary handlers,
    CashuPaymentMethodHandler handler,
    CashuPaymentService cashuPaymentService,
    CashuDbContextFactory cashuDbContextFactory,
    MintManager mintManager,
    StatefulWalletFactory walletFactory,
    FailedTransactionsPoller failedTransactionsPoller,
    ILogger<UICashuWalletController> logger)
    : Controller
{
    private StoreData StoreData => HttpContext.GetStoreData();

    /// <summary>
    /// Api route for fetching current store Cashu Wallet view - All stored proofs grouped by mint and unit which can be exported.
    /// </summary>
    /// <returns></returns>
    [HttpGet("{storeId}/cashu/wallet")]
    public async Task<IActionResult> CashuWallet(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();
        if (!db.CashuWalletConfig.Any(cwc => cwc.StoreId == StoreData.Id))
        {
            return RedirectToAction(
                "GettingStarted",
                "UICashuOnboarding",
                new { storeId = StoreData.Id }
            );
            ;
        }

        var storeKeysetIds = await db.Proofs
            .Where(p => p.StoreId == StoreData.Id && p.Status == ProofState.Available)
            .Select(p => p.Id)
            .Distinct()
            .ToListAsync();

        var mints = await db.MintKeys
            .Where(mk => storeKeysetIds.Contains(mk.KeysetId))
            .Select(mk => mk.Mint.Url)
            .Distinct()
            .ToListAsync();
        var proofsWithUnits = new List<(string Mint, string Unit, ulong Amount)>();

        var unavailableMints = new List<string>();

        foreach (var mint in mints)
        {
            try
            {
                using var cashuHttpClient = CashuUtils.GetCashuHttpClient(mint);
                var keysets = await cashuHttpClient.GetKeysets();

                var localProofs = await db
                    .Proofs.Where(p =>
                        keysets.Keysets.Select(k => k.Id).Contains(p.Id)
                        && p.StoreId == StoreData.Id
                        && p.Status == ProofState.Available
                    )
                    .ToListAsync();

                foreach (var proof in localProofs)
                {
                    var matchingKeyset = keysets.Keysets.FirstOrDefault(k => k.Id == proof.Id);
                    if (matchingKeyset != null)
                    {
                        proofsWithUnits.Add((Mint: mint, matchingKeyset.Unit, proof.Amount));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("(Cashu) Could not load mint {Mint}: {Error}", mint, ex.Message);
                unavailableMints.Add(mint);
            }
        }

        var groupedProofs = proofsWithUnits
            .GroupBy(p => new { p.Mint, p.Unit })
            .Select(group => new
            {
                group.Key.Mint,
                group.Key.Unit,
                Amount = group.Select(x => x.Amount).Sum(),
            })
            .OrderByDescending(x => x.Amount)
            .Select(x => (x.Mint, x.Unit, x.Amount))
            .ToList();

        var exportedTokens = db
            .ExportedTokens.Where(et => et.StoreId == StoreData.Id)
            .OrderByDescending(et => et.CreatedAt)
            .ToList();

        if (unavailableMints.Any())
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Couldn't load {unavailableMints.Count} mints: {String.Join(", ", unavailableMints)}";
        }
        var viewModel = new CashuWalletViewModel
        {
            AvaibleBalances = groupedProofs,
            ExportedTokens = exportedTokens,
        };

        return View("Views/Cashu/CashuWallet.cshtml", viewModel);
    }

    /// <summary>
    /// Api route for exporting stored balance for chosen mint and unit
    /// </summary>
    /// <param name="storeId">Store ID</param>
    /// <param name="mintUrl">Chosen mint url, form which proofs we want to export</param>
    /// <param name="unit">Chosen unit of token</param>
    [HttpPost("{storeId}/cashu/export-mint-balance")]
    public async Task<IActionResult> ExportMintBalance(string storeId, string mintUrl, string unit)
    {
        if (string.IsNullOrWhiteSpace(mintUrl) || string.IsNullOrWhiteSpace(unit))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Invalid mint or unit provided!";
            return RedirectToAction("CashuWallet", new { storeId = StoreData.Id });
        }

        await using var db = cashuDbContextFactory.CreateContext();
        List<GetKeysetsResponse.KeysetItemResponse> keysets;
        try
        {
            var cashuWallet = await walletFactory.CreateAsync(StoreData.Id, mintUrl, unit);
            keysets = await cashuWallet.GetKeysets();
            if (keysets == null || keysets.Count == 0)
            {
                throw new Exception("No keysets were found.");
            }
        }
        catch (Exception)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Couldn't get keysets!";
            return RedirectToAction("CashuWallet", new { storeId = StoreData.Id });
        }

        var selectedProofs = await db
            .Proofs.Where(p =>
                p.StoreId == StoreData.Id
                && keysets.Select(k => k.Id).Contains(p.Id)
                && p.Status == ProofState.Available
            )
            .ToListAsync();

        var createdToken = new CashuToken()
        {
            Tokens =
            [
                new CashuToken.Token
                {
                    Mint = mintUrl,
                    Proofs = selectedProofs.Select(p => p.ToDotNutProof()).ToList(),
                },
            ],
            Memo = "Cashu Token withdrawn from BTCNutServer",
            Unit = unit,
        };

        var tokenAmount = selectedProofs.Select(p => p.Amount).Sum();
        var serializedToken = createdToken.Encode();

        // mark proofs as exported and link to ExportedToken
        foreach (var proof in selectedProofs)
        {
            proof.Status = ProofState.Exported;
        }

        var exportedTokenEntity = new ExportedToken
        {
            SerializedToken = serializedToken,
            Amount = tokenAmount,
            Unit = unit,
            Mint = mintUrl,
            StoreId = StoreData.Id,
            IsUsed = false,
            Proofs = selectedProofs,
        };

        IActionResult result = RedirectToAction(nameof(CashuWallet), new { storeId = StoreData.Id });

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                db.ExportedTokens.Add(exportedTokenEntity);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                result = RedirectToAction(
                    nameof(ExportedToken),
                    new { tokenId = exportedTokenEntity.Id, storeId = StoreData.Id }
                );

            }
            catch
            {
                await transaction.RollbackAsync();
                TempData[WellKnownTempData.ErrorMessage] = "Couldn't export token";
                result = RedirectToAction(nameof(CashuWallet), new { storeId = StoreData.Id });
            }
        });
        return result;
    }

    /// <summary>
    /// Api route for fetching exported token data
    /// </summary>
    /// <param name="storeId">Store ID</param>
    /// <param name="tokenId">Stored Token GUID</param>
    [HttpGet("{storeId}/cashu/token/{tokenId}")]
    public async Task<IActionResult> ExportedToken(string storeId, Guid tokenId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var exportedToken = await db
            .ExportedTokens.Include(et => et.Proofs)
            .SingleOrDefaultAsync(e => e.Id == tokenId);

        if (exportedToken == null)
        {
            return NotFound();
        }

        // in pre-release version there were no Proofs in exportedToken, and token had to be deserialized manually every time
        // this propably can be deleted in future.
        if (exportedToken.Proofs == null || exportedToken.Proofs.Count == 0)
        {
            var deserialized = CashuTokenHelper.Decode(exportedToken.SerializedToken, out _);
            var newProofs = StoredProof
                .FromBatch(
                    deserialized.Tokens.SelectMany(t => t.Proofs).ToList(),
                    storeId,
                    ProofState.Exported
                )
                .ToList();
            exportedToken.Proofs = new();
            exportedToken.Proofs.AddRange(newProofs);
            await db.SaveChangesAsync();
        }


        var model = new ExportedTokenViewModel()
        {
            Amount = exportedToken.Amount,
            Unit = exportedToken.Unit,
            MintAddress = exportedToken.Mint,
            Token = exportedToken.SerializedToken,
        };

        return View("Views/Cashu/ExportedToken.cshtml", model);
    }

    /// <summary>
    /// Api route for fetching failed transactions list
    /// </summary>
    /// <returns></returns>
    [HttpGet("{storeId}/cashu/failed-transactions")]
    public async Task<IActionResult> FailedTransactions(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();
        //fetch recently failed transactions
        var failedTransactions = await db
            .FailedTransactions.Where(ft => ft.StoreId == StoreData.Id)
            .ToListAsync();

        return View("Views/Cashu/FailedTransactions.cshtml", failedTransactions);
    }

    [HttpPost("{storeId}/cashu/failed-transactions")]
    public async Task<IActionResult> PostFailedTransaction(string storeId, Guid failedTransactionId)
    {
        await using var db = cashuDbContextFactory.CreateContext();
        var failedTransaction = db.FailedTransactions.SingleOrDefault(t =>
            t.Id == failedTransactionId
        );

        if (failedTransaction == null)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "Can't get failed transaction with provided GUID!";
            return RedirectToAction("FailedTransactions", new { storeId = StoreData.Id });
        }

        if (failedTransaction.StoreId != StoreData.Id)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "Chosen failed transaction doesn't belong to this store!";
            return RedirectToAction("FailedTransactions", new { storeId = StoreData.Id });
        }

        var invoice = await invoiceRepository.GetInvoice(failedTransaction.InvoiceId);

        if (invoice is null)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "Couldn't find invoice with provided GUID in this store.";
            return RedirectToAction("FailedTransactions", new { storeId = StoreData.Id });
        }

        CashuPaymentService.PollResult pollResult;

        try
        {
            pollResult = await failedTransactionsPoller.PollTransaction(failedTransaction);
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "Couldn't poll failed transaction: " + ex.Message;
            return RedirectToAction("FailedTransactions", new { storeId = StoreData.Id });
        }

        if (!pollResult.Success)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Transaction state: {pollResult.State}. {(pollResult.Error == null ? "" : pollResult.Error.Message)}";
            return RedirectToAction("FailedTransactions", new { storeId = StoreData.Id });
        }

        await cashuPaymentService.RegisterPaymentForFailedTx(failedTransaction);
        TempData[WellKnownTempData.SuccessMessage] =
            $"Transaction retrieved successfully. Marked as paid.";
        return RedirectToAction("FailedTransactions", new { storeId = StoreData.Id });
    }

    [HttpGet("~/cashu/mint-info")]
    public async Task<IActionResult> GetMintInfo(string mintUrl)
    {
        if (
            !mintUrl.StartsWith("http") && !mintUrl.StartsWith("https")
            || !Uri.TryCreate(mintUrl, UriKind.Absolute, out var uri)
        )
        {
            return BadRequest("Invalid mint url provided!");
        }

        try
        {
            var info = await Wallet.Create().WithMint(uri).GetInfo();

            var dto = new
            {
                name = info.Name,
                description = info.Description,
                description_long = info.DescriptionLong,
                contact = new
                {
                    email = info.Contact?.FirstOrDefault(i => i?.Method == "email", null)?.Info,
                    twitter = info.Contact?.FirstOrDefault(i => i?.Method == "twitter", null)?.Info,
                    nostr = info.Contact?.FirstOrDefault(i => i?.Method == "nostr", null)?.Info,
                },
                nuts = info.Nuts?.Keys,
                currency = info.IsSupportedMintMelt(4).Methods.Select(m => m.Unit).Distinct(),
                version = info.Version,
                url = mintUrl,
            };

            return Ok(dto);
        }
        catch
        {
            return NotFound("Failed to fetch mint info");
        }
    }

    [HttpGet("{storeId}/cashu/check-token-states")]
    public async Task<IActionResult> CheckAllTokenStates(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var unspentTokens = await db
            .ExportedTokens.Include(t => t.Proofs)
            .Where(t => t.StoreId == StoreData.Id && !t.IsUsed)
            .ToListAsync();

        if (unspentTokens.Count == 0)
        {
            return RedirectToAction(nameof(CashuWallet), new { storeId });
        }

        var checkTasks = unspentTokens
            .GroupBy(t => t.Mint)
            .Select(async group =>
                {
                    var mint = group.Key;
                    var tokens = group.ToList();

                    try
                    {
                        // we dont care about keyset sync here. nor the unit
                        var wallet = Wallet.Create().WithMint(mint).WithKeysetSync(false);

                        var allProofs = tokens
                            .Where(t => t.Proofs is { Count: > 0 })
                            .SelectMany(t => t.Proofs!)
                            .ToList();

                        if (allProofs.Count == 0)
                        {
                            return (Mint: mint, SpentTokens: new List<ExportedToken>(), Error: (Exception?)null);
                        }

                        // map states 
                        var states = await wallet.CheckState(allProofs);
                        var proofToSpent = allProofs
                            .Zip(states.States,
                                (p, s) => new { ProofId = p.ProofId, Spent = s.State == StateResponseItem.TokenState.SPENT })
                            .ToDictionary(x => x.ProofId, x => x.Spent);

                        var spentTokens = new List<ExportedToken>();
                        foreach (var token in tokens.Where(t => t.Proofs is { Count: > 0 }))
                        {
                            if (token.Proofs.Any(p => proofToSpent[p.ProofId]))
                                spentTokens.Add(token);
                        }

                        return (Mint: mint, SpentTokens: spentTokens, Error: null);
                    }
                    catch (Exception ex)
                    {
                        return (Mint: mint, SpentTokens: new List<ExportedToken>(), Error: ex);
                    }
                });

        var results = await Task.WhenAll(checkTasks);

        var tokensToMarkAsSpent = new List<ExportedToken>();
        var failedMints = new List<string>();

        foreach (var (mint, spentTokens, error) in results)
        {
            if (error != null)
            {
                logger.LogDebug("(Cashu) Failed to check mint {Mint}: {Message}", mint, error.Message);
                failedMints.Add(mint);
                continue;
            }
            tokensToMarkAsSpent.AddRange(spentTokens);
        }

        if (tokensToMarkAsSpent.Count > 0)
        {
            foreach (var token in tokensToMarkAsSpent)
            {
                token.IsUsed = true;
                if (token.Proofs != null)
                {
                    foreach (var proof in token.Proofs)
                    {
                        // we don't care about partial spending yet. 
                        proof.Status = ProofState.Spent;
                    }
                }
            }
            await db.SaveChangesAsync();
            TempData[WellKnownTempData.SuccessMessage] = $"Marked {tokensToMarkAsSpent.Count} token(s) as spent.";
        }

        if (failedMints.Count > 0)
            TempData[WellKnownTempData.ErrorMessage] = $"Failed {failedMints.Count} mint(s): {string.Join(", ", failedMints)}";

        return RedirectToAction(nameof(CashuWallet), new { storeId });
    }


    [HttpGet("{storeId}/cashu/remove-spent-proofs")]
    public async Task<IActionResult> RemoveSpentProofs(string storeId)
    {
        await using var db = cashuDbContextFactory.CreateContext();

        var proofsToCheck = await db
            .Proofs.Where(p =>
                p.StoreId == StoreData.Id &&
                (p.Status == ProofState.Available)
            )
            .ToListAsync();

        if (proofsToCheck.Count == 0)
        {
            TempData[WellKnownTempData.SuccessMessage] = "No proofs to check.";
            return RedirectToAction(nameof(CashuWallet), new { storeId });
        }

        var keysetIds = proofsToCheck.Select(p => p.Id).Distinct();
        var keysetToMintMap = await mintManager.MapKeysetIdsToMints(keysetIds);

        var proofsByMintAndUnit = proofsToCheck
            .Where(p => keysetToMintMap.ContainsKey(p.Id.ToString()))
            .GroupBy(p => keysetToMintMap[p.Id.ToString()])
            .ToList();

        var checkTasks = proofsByMintAndUnit.Select(async group =>
        {
            var (mintUrl, unit) = group.Key;
            var proofs = group.ToList();

            try
            {
                var wallet = Wallet.Create().WithMint(mintUrl).WithKeysetSync(false);
                var dotnutProofs = proofs.Select(p => p.ToDotNutProof()).ToList();
                var states = await wallet.CheckState(dotnutProofs);

                var spentProofs = proofs
                    .Zip(states.States, (proof, state) => (proof, state))
                    .Where(x => x.state.State == StateResponseItem.TokenState.SPENT)
                    .Select(x => x.proof)
                    .ToList();

                return (Mint: mintUrl, SpentProofs: spentProofs, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Mint: mintUrl, SpentProofs: new List<StoredProof>(), Error: ex);
            }
        });

        var results = await Task.WhenAll(checkTasks);

        var proofsToRemove = new List<StoredProof>();
        var failedMints = new List<string>();

        foreach (var (mint, spentProofs, error) in results)
        {
            if (error != null)
            {
                logger.LogDebug("(Cashu) Failed to check proof states for mint {Mint}: {Message}", mint, error.Message);
                failedMints.Add(mint);
                continue;
            }

            proofsToRemove.AddRange(spentProofs);
        }

        if (proofsToRemove.Count > 0)
        {
            db.Proofs.RemoveRange(proofsToRemove);
            await db.SaveChangesAsync();

            TempData[WellKnownTempData.SuccessMessage] =
                $"Removed {proofsToRemove.Count} spent proof(s) from database.";
        }
        else
        {
            TempData[WellKnownTempData.SuccessMessage] = "No spent proofs found.";
        }

        if (failedMints.Count > 0)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Couldn't reach {failedMints.Count} mint(s): {string.Join(", ", failedMints)}";
        }

        return RedirectToAction(nameof(CashuWallet), new { storeId });
    }
}
