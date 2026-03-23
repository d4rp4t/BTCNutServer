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
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
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
        StoreRepository storeRepository,
        CashuDbContextFactory cashuDbContextFactory,
        PaymentMethodHandlerDictionary handlers,
        RestoreService restoreService
    )
    {
        _storeRepository = storeRepository;
        _cashuDbContextFactory = cashuDbContextFactory;
        _restoreService = restoreService;
        _handlers = handlers;
    }

    private StoreData StoreData => HttpContext.GetStoreData();

    private readonly StoreRepository _storeRepository;
    private readonly CashuDbContextFactory _cashuDbContextFactory;
    private readonly RestoreService _restoreService;
    private readonly PaymentMethodHandlerDictionary _handlers;

    [HttpGet("getting-started")]
    public async Task<IActionResult> GettingStarted(string storeId)
    {
        await using var db = _cashuDbContextFactory.CreateContext();
        if (StoreData == null || db.CashuWalletConfig.Any(cwc => cwc.StoreId == StoreData.Id))
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

        if (invalidWordIndices.Count > 0 || invalidMintsIndices.Count > 0)
        {
            model.InvalidWordIndices = invalidWordIndices;
            model.InvalidMintsIndices = invalidMintsIndices;
            StringBuilder msg = new StringBuilder();
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
                ReturnUrl = Url.Action("CashuWallet","UICashuWallet", new { storeId = StoreData.Id }),
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
        await using var db = _cashuDbContextFactory.CreateContext();
        var userMnemonic = await db.CashuWalletConfig.SingleOrDefaultAsync(cwc =>
            cwc.StoreId == StoreData.Id
        );
        if (userMnemonic == null || userMnemonic.Verified)
        {
            return NotFound();
        }

        var validChunk = string.Join("", userMnemonic.WalletMnemonic.Words.Take(4));
        if (!Equals(validChunk, fourWordChunk))
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Invalid words chosen. Try again";
            return RedirectToAction("ConfirmMnemonic", new { storeId = StoreData.Id });
        }
        userMnemonic.Verified = true;
        await db.SaveChangesAsync();

        TempData[WellKnownTempData.SuccessMessage] = $"Wallet created and verified successfully!";
        var hasLightning = StoreData.IsLightningEnabled("BTC");
        if (!hasLightning)
        {
            return RedirectToAction("InitWithoutLightning", new { storeId = StoreData.Id });
        }
        return RedirectToAction("StoreConfig", "UICashuStores", new { storeId = StoreData.Id });
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
