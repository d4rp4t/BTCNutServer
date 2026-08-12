using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Services;
using BTCPayServer.Plugins.Cashu.ViewModels;
using DotNut.NBitcoin.BIP39;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Cashu.Controllers;

[Route("stores/{storeId}/cashu")]
[Authorize(
    Policy = Policies.CanModifyStoreSettings,
    AuthenticationSchemes = AuthenticationSchemes.Cookie
)]
public class UICashuOnboardingController : Controller
{
    public UICashuOnboardingController(
        CashuDbContextFactory cashuDbContextFactory,
        RestoreService restoreService
    )
    {
        _cashuDbContextFactory = cashuDbContextFactory;
        _restoreService = restoreService;
    }

    private StoreData? StoreData => HttpContext.GetStoreDataOrNull();

    private readonly CashuDbContextFactory _cashuDbContextFactory;
    private readonly RestoreService _restoreService;

    [HttpGet("getting-started")]
    public async Task<IActionResult> GettingStarted(string storeId)
    {
        await using var db = _cashuDbContextFactory.CreateContext();
        if (StoreData == null || await db.CashuWalletConfig.AnyAsync(cwc => cwc.StoreId == StoreData.Id))
        {
            return NotFound();
        }

        var model = new GettingStartedViewModel() { StoreId = StoreData.Id };

        return View("Views/Cashu/Onboarding/GettingStarted.cshtml", model);
    }

    [HttpGet("restore-wallet")]
    public async Task<IActionResult> RestoreFromMnemonic(
        string storeId,
        WalletRestoreViewModel? model
    )
    {
        return View("Views/Cashu/Onboarding/RestoreFromMnemonic.cshtml", model);
    }

    [HttpPost("restore-wallet")]
    public async Task<IActionResult> Restore(string storeId, WalletRestoreViewModel model)
    {
        if (StoreData?.Id == null)
        {
            return NotFound();
        }

        //validate wordlist
        Wordlist wordlist = Wordlist.English;
        var wordSet = new HashSet<string>(wordlist.GetWords());

        var invalidWordIndices = new HashSet<int>();
        var invalidMintsIndices = new HashSet<int>();

        for (var i = 0; i < model.Words.Count; i++)
        {
            var modelWord = model.Words[i];
            if (!wordSet.Contains(modelWord))
            {
                invalidWordIndices.Add(i);
            }
        }

        for (var i = 0; i < model.MintUrls.Count; i++)
        {
            var raw = model.MintUrls[i].Trim();
            if (
                !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            )
            {
                invalidMintsIndices.Add(i);
                continue;
            }
            model.MintUrls[i] = MintManager.NormalizeMintUrl(raw);
        }

        // the wordlist check above passes for any in-wordlist typo - the BIP39 checksum is what
        // catches a swapped or mistyped word (~15 out of 16 of them)
        string? mnemonicError = null;
        if (invalidWordIndices.Count == 0)
        {
            try
            {
                if (!new Mnemonic(model.Mnemonic ?? string.Empty, wordlist).IsValidChecksum)
                {
                    mnemonicError =
                        "Invalid seed phrase checksum - check the words and their order.";
                }
            }
            catch (FormatException)
            {
                mnemonicError = "A seed phrase has to be 12, 15, 18, 21 or 24 words long.";
            }
        }

        if (invalidWordIndices.Count > 0 || invalidMintsIndices.Count > 0 || mnemonicError != null)
        {
            model.InvalidWordIndices = invalidWordIndices;
            model.InvalidMintsIndices = invalidMintsIndices;
            StringBuilder msg = new StringBuilder();
            if (mnemonicError != null)
            {
                msg.AppendLine(mnemonicError);
            }

            if (invalidWordIndices.Count > 0)
            {
                msg.AppendLine(
                    $"Invalid word indices: {string.Join(",", invalidWordIndices.Select(i => i + 1))}"
                );
            }

            if (invalidMintsIndices.Count > 0)
            {
                msg.AppendLine(
                    $"Invalid mint indices: {string.Join(",", invalidMintsIndices.Select(i => i + 1))}"
                );
            }

            TempData[WellKnownTempData.ErrorMessage] = msg.ToString();

            return View("Views/Cashu/Onboarding/RestoreFromMnemonic.cshtml", model);
        }

        await using (var walletDb = _cashuDbContextFactory.CreateContext())
        {
            // restoring a different seed over a wallet in use would replace the seed every proof
            // is derived from, and the operator's backup would no longer recover anything received
            // afterwards. re-running the restore with the same seed (the "try again" link on a
            // partially failed job) is fine, and so is replacing an unverified config - that one
            // is an abandoned create-mnemonic flow.
            var existingConfig = await walletDb.CashuWalletConfig.SingleOrDefaultAsync(cwc =>
                cwc.StoreId == StoreData.Id
            );
            if (existingConfig is { Verified: true }
                && !string.Equals(existingConfig.WalletMnemonic?.ToString(),
                    new Mnemonic(model.Mnemonic, wordlist).ToString(), StringComparison.Ordinal))
            {
                TempData[WellKnownTempData.ErrorMessage] =
                    "This store already has a Cashu wallet with a different seed phrase. "
                    + "Delete the wallet before restoring another one.";
                return RedirectToAction(
                    "CashuWallet",
                    "UICashuWallet",
                    new { storeId = StoreData.Id }
                );
            }
        }

        var jobId = _restoreService.QueueRestore(StoreData.Id, model.MintUrls, model.Mnemonic);
        return RedirectToAction(nameof(RestoreStatus), new { storeId = StoreData.Id, jobId });
    }

    [HttpGet("restore-status/{jobId}")]
    public async Task<IActionResult> RestoreStatus(string storeId, string jobId)
    {
        var status = _restoreService.GetRestoreStatus(jobId);
        if (status == null)
        {
            return NotFound();
        }
        return View("Views/Cashu/Onboarding/RestoreStatus.cshtml", status);
    }

    [HttpGet("create-mnemonic")]
    public async Task<IActionResult> CreateMnemonic(string storeId)
    {
        await using var db = _cashuDbContextFactory.CreateContext();
        if (StoreData?.Id == null)
        {
            return NotFound();
        }
        // in case of user coming back...
        var existingMnemonic = await db.CashuWalletConfig.SingleOrDefaultAsync(cwc =>
            cwc.StoreId == StoreData.Id
        );
        if (existingMnemonic != null)
        {
            var existingModel = new RecoverySeedBackupViewModel()
            {
                CryptoCode = "CASHU",
                IsStored = true,
                Mnemonic = existingMnemonic.WalletMnemonic.ToString(),
                RequireConfirm = false,
                ReturnUrl = Url.Action("CashuWallet", "UICashuWallet", new { storeId = StoreData.Id }),
            };
            return View("Views/Cashu/Onboarding/CreateMnemonic.cshtml", existingModel);
        }
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var walletConfig = new CashuWalletConfig
        {
            StoreId = StoreData.Id,
            WalletMnemonic = mnemonic,
        };
        db.CashuWalletConfig.Add(walletConfig);
        await db.SaveChangesAsync();

        var model = new RecoverySeedBackupViewModel()
        {
            CryptoCode = "CASHU",
            IsStored = true,
            Mnemonic = walletConfig.WalletMnemonic.ToString(),
            RequireConfirm = true,
            ReturnUrl = Url.Action("ConfirmMnemonic", new { storeId = StoreData.Id }),
        };
        return View("Views/Cashu/Onboarding/CreateMnemonic.cshtml", model);
    }

    [HttpGet("confirm-mnemonic")]
    public async Task<IActionResult> ConfirmMnemonic(string storeId)
    {
        await using var db = _cashuDbContextFactory.CreateContext();
        if (StoreData == null)
        {
            return NotFound();
        }

        var userMnemonic = await db.CashuWalletConfig.SingleOrDefaultAsync(cwc =>
            cwc.StoreId == StoreData.Id
        );
        if (userMnemonic == null || userMnemonic.Verified)
        {
            return NotFound();
        }

        var randomMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);

        var rand = new Random();
        var randomList = new List<string>();
        randomList.AddRange(userMnemonic.WalletMnemonic.Words.Take(4));
        randomList.AddRange(randomMnemonic.Words.Take(8));
        randomList = randomList.OrderBy(_ => rand.Next()).ToList();

        var model = new ConfirmMnemonicViewModel
        {
            Mnemonic = userMnemonic.WalletMnemonic.ToString(),
            Words = randomList,
            ViewMnemonicUrl = Url.Action("ConfirmMnemonic", new { storeId = StoreData.Id }),
        };

        return View("Views/Cashu/Onboarding/ConfirmMnemonic.cshtml", model);
    }

    [HttpPost("confirm-mnemonic")]
    public async Task<IActionResult> ConfirmMnemonic(string storeId, string fourWordChunk)
    {
        if (StoreData is not { } store) return NotFound();
        await using var db = _cashuDbContextFactory.CreateContext();
        var userMnemonic = await db.CashuWalletConfig.SingleOrDefaultAsync(cwc =>
            cwc.StoreId == store.Id
        );
        if (userMnemonic == null || userMnemonic.Verified)
        {
            return NotFound();
        }

        var validChunk = string.Join("", userMnemonic.WalletMnemonic.Words.Take(4));
        if (!Equals(validChunk, fourWordChunk))
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Invalid words chosen. Try again";
            return RedirectToAction("ConfirmMnemonic", new { storeId = store.Id });
        }
        userMnemonic.Verified = true;
        await db.SaveChangesAsync();

        TempData[WellKnownTempData.SuccessMessage] = $"Wallet created and verified successfully!";
        var hasLightning = store.IsLightningEnabled("BTC");
        if (!hasLightning)
        {
            return RedirectToAction("InitWithoutLightning", new { storeId = store.Id });
        }
        return RedirectToAction("StoreConfig", "UICashuStores", new { storeId = store.Id });
    }

    [HttpGet("init-without-lightning")]
    public async Task<IActionResult> InitWithoutLightning(string storeId)
    {
        if (StoreData?.Id == null)
        {
            return NotFound();
        }

        var model = new CashuInitWithoutLightningViewModel
        {
            TrustedMintsUrls = string.Empty,
            PaymentAcceptanceModel = CashuPaymentModel.TrustedMintsOnly,
            ReturnUrl = Url.Action("StoreConfig", "UICashuStores", new { storeId = StoreData.Id }),
        };
        return View("Views/Cashu/Onboarding/InitWithoutLightning.cshtml", model);
    }
}
