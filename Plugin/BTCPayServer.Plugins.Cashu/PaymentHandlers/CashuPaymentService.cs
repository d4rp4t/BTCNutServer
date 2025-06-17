using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.Data.Models;
using BTCPayServer.Plugins.Cashu.Errors;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using DotNut;
using DotNut.Api;
using DotNut.ApiModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using InvoiceStatus = BTCPayServer.Client.Models.InvoiceStatus;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;

public class CashuPaymentService
{
    private readonly CashuDbContextFactory _cashuDbContextFactory;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly LightningClientFactoryService _lightningClientFactoryService;
    private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
    private readonly Logs _logs;
    private readonly PaymentService _paymentService;
    private readonly StoreRepository _storeRepository;
    
    public CashuPaymentService(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        LightningClientFactoryService lightningClientFactoryService,
        IOptions<LightningNetworkOptions> lightningNetworkOptions,
        CashuDbContextFactory cashuDbContextFactory,
        Logs logs)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _paymentService = paymentService;
        _handlers = handlers;
        _lightningClientFactoryService = lightningClientFactoryService;
        _lightningNetworkOptions = lightningNetworkOptions;
        _cashuDbContextFactory = cashuDbContextFactory;
        _logs = logs;
    }

    
    /// <summary>
    /// Processing the payment from user input;
    /// </summary>
    /// <param name="token">v4 Cashu Token</param>
    /// <param name="invoiceId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task ProcessPaymentAsync(
        CashuToken token,
        string invoiceId,
        CancellationToken cancellationToken = default
        )
    {
        _logs.PayServer.LogInformation($"(Cashu) Processing payment for invoice {invoiceId}");

        var invoice = await _invoiceRepository.GetInvoice(invoiceId, true);

        if (invoice == null)
            throw new CashuPaymentException("Invalid invoice");
                
        var storeData = await _storeRepository.FindStore(invoice.StoreId);

        var cashuPaymentMethodConfig =
            storeData?.GetPaymentMethodConfig<CashuPaymentMethodConfig>(CashuPlugin.CashuPmid, _handlers);
        if (cashuPaymentMethodConfig == null)
        {
            _logs.PayServer.LogError("(Cashu) Couldn't get Cashu Payment method config");
            throw new CashuPaymentException("Coudldn't process the payment. Token wasn't spent");
        }

        var handler = _handlers[CashuPlugin.CashuPmid];
        if (handler is not (IHasNetwork hasNetwork and CashuPaymentMethodHandler))
        {
            _logs.PayServer.LogError("(Cashu) Invalid Cashu Payment method handler");
            throw new CashuPaymentException("Coudldn't process the payment. Token wasn't spent");
        }
        
        var network = hasNetwork.Network;
        
        decimal singleUnitSatoshiWorth;
        try
        {
            singleUnitSatoshiWorth = await CashuUtils.GetTokenSatRate(token, network.NBitcoinNetwork);
        }
        catch (HttpRequestException)
        {
            _logs.PayServer.LogError("(Cashu) Couldn't connect to: {mint}",token.Tokens.First().Mint );
            throw new CashuPaymentException("Mint unreachable.");
        }
        catch (CashuProtocolException ex)
        {
            _logs.PayServer.LogError("(Cashu) Protocol error occurred while processing {invoiceId} invoice; {ex}", invoiceId, ex.Message);
            throw new CashuPaymentException(ex.Message, ex); 
        }
        catch (Exception ex)
        {
            _logs.PayServer.LogError("(Cashu) Couldn't fetch token/sat rate for invoice {invoiceId}", invoiceId);
            throw new CashuPaymentException("Coudldn't process the payment. Can't fetch token/satoshi rate from mint");
        }
        
        var invoiceAmount =Money.Coins( 
            invoice.GetPaymentPrompt(CashuPlugin.CashuPmid)?.Calculate().Due ?? invoice.Price
        );
        
        var simplifiedToken = CashuUtils.SimplifyToken(token);
        var providedAmount = Money.Satoshis(
            Math.Floor(Convert.ToDecimal(simplifiedToken.SumProofs * singleUnitSatoshiWorth))
            );
        
        if (providedAmount < invoiceAmount)
        {
            _logs.PayServer.LogError("(Cashu) Insufficient token worth for invoice {invoiceId}. Expected {invoiceSats}, calculated {calculated}.", invoiceId, invoiceAmount.Satoshi, providedAmount.Satoshi );
            throw new CashuPaymentException("Insufficient token value.");
        }
        
        _logs.PayServer.LogInformation(
            "(Cashu) Processing Cashu payment. Invoice: {InvoiceId}, Store: {StoreId}, Amount: {AmountSats} sat", 
            invoiceId,
            invoice.StoreId, 
            invoiceAmount.Satoshi
        );
        
        if (cashuPaymentMethodConfig.TrustedMintsUrls.Contains(simplifiedToken.Mint))
        {
            var wallet = new CashuWallet(simplifiedToken.Mint, simplifiedToken.Unit, _cashuDbContextFactory);
            await EnsureTokenSpendable(wallet, simplifiedToken.Proofs);
            await HandleSwapOperation(
                wallet,
                invoice, 
                storeData, 
                simplifiedToken, 
                cashuPaymentMethodConfig.FeeConfing, 
                handler as CashuPaymentMethodHandler, 
                providedAmount.Satoshi,
                cancellationToken
                );
            return;
        }

        switch (cashuPaymentMethodConfig.PaymentModel)
        {
            case CashuPaymentModel.MeltImmediately:
            {
                var lnClient = GetStoreLightningClient(storeData, network);
                var wallet = new CashuWallet(lnClient, simplifiedToken.Mint, simplifiedToken.Unit, _cashuDbContextFactory);
                await EnsureTokenSpendable(wallet, simplifiedToken.Proofs);
                await HandleMeltOperation(
                    wallet,
                    invoice,
                    storeData,
                    simplifiedToken,
                    handler as CashuPaymentMethodHandler,
                    singleUnitSatoshiWorth,
                    cashuPaymentMethodConfig.FeeConfing
                    );
                return;
            }
            case CashuPaymentModel.SwapAndHodl:
            {
                throw new CashuPaymentException("Can't process this payment. Merchant can't trust this mint.");
            }
            default:
            {
                throw new Exception("Unknown cashu payment model");
            }
        }
    }


    
    /// <summary>
    /// Abstraction for handling Swap Operations
    /// </summary>
    /// <param name="wallet">CashuWallet Instance</param>
    /// <param name="storeId"></param>
    /// <param name="token">SimplifiedToken</param>
    /// <param name="feeConfig">Fee configuration</param>
    /// <exception cref="CashuPaymentException">Error with message passed to UI</exception>
    private async Task HandleSwapOperation(
        CashuWallet wallet,
        InvoiceEntity invoice,
        StoreData store,
        CashuUtils.SimplifiedCashuToken token,
        CashuFeeConfig feeConfig,
        CashuPaymentMethodHandler handler,
        decimal tokenSatoshiWorth,
        CancellationToken cts = default
    )
    {
        List<GetKeysetsResponse.KeysetItemResponse> keysets = null;
        try
        {
            keysets = await wallet.GetKeysets();
            if (keysets == null)
            {
                throw new Exception("No keysets found.");
            }
        }
        catch (Exception ex)
        {
            _logs.PayServer.LogError("(Cashu) Couldn't get keysets. Funds weren't spent.");
            throw new CashuPaymentException("Could not get keysets!", ex);
        }

        if (!CashuUtils.ValidateFees(token.Proofs, feeConfig, keysets,
                out var keysetFee))
        {
            _logs.PayServer.LogError("(Cashu) Keyset fees bigger than configured limit! {fee} Token wasn't spent. ", keysetFee);
            throw new CashuPaymentException("Fees too big!");
        }
        
        _logs.PayServer.LogDebug(
            "(Cashu) Swap initiated. Mint: {MintUrl}, InputProofs: {ProofCount}, Fee: {FeeSats} sat", 
            token.Mint, 
            token.Proofs.Count, 
            keysetFee
        );
        
        var swapResult = await wallet.Receive(token.Proofs, keysetFee);
        
        //handle swap errors
        if (!swapResult.Success)
        {
            switch (swapResult.Error)
            {
                case CashuProtocolException cpe:
                    throw new CashuPaymentException(cpe.Message);
                
                case CashuPaymentException cpe:
                    throw cpe;
                
                case HttpRequestException httpException:
                {
                    var ftx = new FailedTransaction()
                    {
                        InvoiceId = invoice.Id,
                        StoreId = invoice.StoreId,
                        LastRetried = DateTimeOffset.Now.ToUniversalTime(),
                        MintUrl = token.Mint,
                        UsedProofs = StoredProof.FromBatch(token.Proofs, invoice.StoreId).ToList(),
                        OperationType = OperationType.Swap,
                        OutputData = swapResult.ProvidedOutputs,
                        Unit = token.Unit,
                        RetryCount = 0,
                        Details = "Connection with mint broken while swap",
                    };
                    var pollResult = await PollFailedSwap(ftx, store, cts);

                    if (!pollResult.Success)
                    {
                        ftx.RetryCount +=1;
                        ftx.LastRetried = DateTimeOffset.Now.ToUniversalTime();
                        await using var db = _cashuDbContextFactory.CreateContext();
                        await db.FailedTransactions.AddAsync(ftx, cts);
                        _logs.PayServer.LogError("(Cashu) Transaction {id} failed because of broken connection with mint. See Failed Transactions in settings.", invoice.Id);
                        await db.SaveChangesAsync(cts);
                        return;
                    }
                    
                    await AddProofsToDb(pollResult.ResultProofs!, ftx.StoreId, ftx.MintUrl);
                    await RegisterCashuPayment(invoice, handler, Money.Satoshis(tokenSatoshiWorth));
                    break;
                }
            }
        }
        
        var returnedAmount = swapResult.ResultProofs!.Select(p => p.Amount).Sum();
        _logs.PayServer.LogInformation(
            "(Cashu) Swap operation success. {amount} {unit} received.", 
            returnedAmount, 
            token.Unit
        );
        if (returnedAmount < token.SumProofs - keysetFee)
        {
            var ftx = new FailedTransaction()
            {
                InvoiceId = invoice.Id,
                StoreId = invoice.StoreId,
                LastRetried = DateTimeOffset.Now.ToUniversalTime(),
                MintUrl = token.Mint,
                UsedProofs = StoredProof.FromBatch(token.Proofs, invoice.StoreId).ToList(),
                OperationType = OperationType.Swap,
                OutputData = swapResult.ProvidedOutputs,
                Unit = token.Unit,
                RetryCount = 0,
                Details = "Mint Returned less signatures than was requested. Even though, merchant received the payment"
            };
            _logs.PayServer.LogError("(Cashu) Mint returned less signatures than requested for transaction {tx}. Merchant received payment, but still marked as unpaid.", invoice.Id);
            //TODO: Pay partially
        }
        await AddProofsToDb(swapResult.ResultProofs, invoice.StoreId, token.Mint);
        await RegisterCashuPayment(invoice, handler, Money.Satoshis(tokenSatoshiWorth));
    }


    /// <summary>
    /// Handles melt operation with retry
    /// </summary>
    private async Task HandleMeltOperation(
        CashuWallet wallet,
        InvoiceEntity invoice,
        StoreData store,
        CashuUtils.SimplifiedCashuToken token,
        CashuPaymentMethodHandler handler,
        decimal unitPrice,
        CashuFeeConfig feeConfig)
    {
        if (!wallet.HasLightningClient)
        {
            _logs.PayServer.LogError("Could not find lightning client!");
            throw new CashuPluginException("Could not find lightning client!");
        }
        
        List<GetKeysetsResponse.KeysetItemResponse> keysets;
        try
        {
            keysets = await wallet.GetKeysets();
            if (keysets == null)
            {
                throw new Exception();
            }
        }
        catch (Exception ex)
        {
            _logs.PayServer.LogError("(Cashu) Couldn't get keysets. Funds weren't spent.");
            throw new CashuPaymentException("Could not get keysets!", ex);
        }
        
        var meltQuoteResponse = await wallet.CreateMeltQuote(token, unitPrice, keysets);
        if (!meltQuoteResponse.Success)
        {
            _logs.PayServer.LogError("Could not create melt quote!" );
            if (meltQuoteResponse.Error != null)
            {
                _logs.PayServer.LogError("Exception: {ex}", meltQuoteResponse.Error );
            }
            throw new CashuPaymentException("Could not create melt quote!");
        }
        
        if (!CashuUtils.ValidateFees(token.Proofs, feeConfig, meltQuoteResponse.KeysetFee!.Value, (ulong)meltQuoteResponse.MeltQuote!.FeeReserve))
        {
            _logs.PayServer.LogError("(Cashu) Fees bigger than configured limit! Lightning fee: {ln}, keyset fee: {keysetfee}.", (ulong)meltQuoteResponse.MeltQuote!.FeeReserve, meltQuoteResponse.KeysetFee!.Value);
            throw new CashuPaymentException("Fees are too big.");
        }
        
        _logs.PayServer.LogInformation(
            "(Cashu) Melt operation started. Invoice: {InvoiceId}, LightningFee: {LightningFee}, KeysetFee: {KeysetFee}", 
            invoice.Id, 
            meltQuoteResponse.MeltQuote.FeeReserve, 
            meltQuoteResponse.KeysetFee
        );
        
        var meltResponse = await wallet.Melt(meltQuoteResponse.MeltQuote, token.Proofs);
        
        if (meltResponse.Success)
        {
            var lnInvPaid = await wallet.ValidateLightningInvoicePaid(meltQuoteResponse.Invoice?.Id);
            
            if (!lnInvPaid)
            {
                var ftx = new FailedTransaction
                {
                    StoreId = invoice.StoreId,                 
                    InvoiceId = invoice.Id,
                    LastRetried = DateTimeOffset.Now.ToUniversalTime(),
                    MintUrl = token.Mint,
                    Unit = token.Unit,
                    UsedProofs = StoredProof.FromBatch(token.Proofs, invoice.StoreId).ToList(),
                    OperationType = OperationType.Melt,
                    OutputData = meltResponse.BlankOutputs,
                    MeltDetails = new MeltDetails
                    {
                        Expiry = DateTimeOffset.FromUnixTimeSeconds(meltQuoteResponse.MeltQuote.Expiry),
                        LightningInvoiceId = meltQuoteResponse.Invoice!.Id,
                        MeltQuoteId = meltResponse.Quote!.Quote,
                        //Assert status as pending, even if it's paid - lightning invocie has to be paid 
                        Status = "PENDING"
                    },
                    RetryCount = 1,
                    Details = "Mint marked melt quote as paid, but lightning invoice is still unpaid.",
                };
                await using var ctx = _cashuDbContextFactory.CreateContext();
                ctx.FailedTransactions.Add(ftx);
                await ctx.SaveChangesAsync();
                _logs.PayServer.LogError("(Cashu) Mint marked melt quote as paid, but lightning invoice is still unpaid. Please verify transaction manually.");
                throw new CashuPaymentException($"There was a problem processing your request. Please contact the merchant with corresponding invoice Id: {invoice.Id}");
            }


            var amountMelted = Money.Satoshis(meltQuoteResponse.Invoice.Amount.ToUnit(LightMoneyUnit.Satoshi));
            var overpaidFeesReturned = Money.Satoshis(meltResponse.ChangeProofs?.Select(p=>p.Amount).Sum()*unitPrice??0);
            var amountPaid =  amountMelted + overpaidFeesReturned; 
            
            //add overpaid ln fees proofs to the db and register payment
            await AddProofsToDb(meltResponse.ChangeProofs, store.Id, token.Mint);
            await RegisterCashuPayment(invoice, handler, amountPaid); 
            
            _logs.PayServer.LogInformation(
                "(Cashu) Melt operation success. Melted: {amountMelted} sat, Overpaid lightning fees returned: {overpaidFeesReturned} sat. Total: {total} sat", 
                amountMelted.Satoshi, 
                overpaidFeesReturned.Satoshi, 
                amountPaid.Satoshi
            );
        }

        if (meltResponse.Error is CashuProtocolException)
        {
            _logs.PayServer.LogError(
                "(Cashu) Melt Error: {Error}",meltResponse.Error.Message 
            );
            throw new CashuPaymentException("Could not process melt!");
            
            
        }

        if (meltResponse.Error is HttpRequestException)
        {
            var ftx = new FailedTransaction
            {
                StoreId = store.Id,                 
                InvoiceId = invoice.Id,
                LastRetried = DateTimeOffset.Now.ToUniversalTime(),
                MintUrl = token.Mint,
                Unit = token.Unit,
                UsedProofs = StoredProof.FromBatch(token.Proofs, store.Id).ToList(),
                OperationType = OperationType.Melt,
                OutputData = meltResponse.BlankOutputs,
                MeltDetails = new MeltDetails
                {
                    Expiry = DateTimeOffset.FromUnixTimeSeconds(meltQuoteResponse.MeltQuote.Expiry),
                    LightningInvoiceId = meltQuoteResponse.Invoice!.Id,
                    MeltQuoteId = meltResponse.Quote!.Quote,
                    //Assert status as pending, even if it's paid - lightning invoice has to be paid 
                    Status = "PENDING"
                },
                RetryCount = 1
            };
            try
            {
                //retry
                var state = await wallet.CheckTokenState(token.Proofs);
                if (state == StateResponseItem.TokenState.UNSPENT)
                {
                    throw new CashuPaymentException("Could not process melt!");
                }
                
                var failedMeltState = await PollFailedMelt(ftx, store, handler);
                
                if (failedMeltState.State == CashuPaymentState.Failed)
                {
                    throw new CashuPaymentException("Could not process melt!");
                }
            }
            catch (HttpRequestException)
            {
                _logs.PayServer.LogError("Network error occured while processing melt {txId}. Please verify transaction manually", invoice.Id);
                await wallet.CheckTokenState(token.Proofs);
                var db = _cashuDbContextFactory.CreateContext();
                await db.FailedTransactions.AddAsync(ftx);
                await db.SaveChangesAsync();
            }
        }
    }
    
    public async Task RegisterCashuPayment(InvoiceEntity invoice, CashuPaymentMethodHandler handler, Money amount, bool markPaid = true)
    {
        //set payment method fee to 0 so it won't be added to due for second time
        var prompt = invoice.GetPaymentPrompt(CashuPlugin.CashuPmid);
        prompt.PaymentMethodFee = 0.0m;
        await _invoiceRepository.UpdatePrompt(invoice.Id, prompt);
        
        var paymentData = new PaymentData
        {
            Id = Guid.NewGuid().ToString(),
            Created = DateTimeOffset.UtcNow,
            Status = PaymentStatus.Processing,
            Currency = "BTC",
            InvoiceDataId = invoice.Id,
            Amount = amount.ToDecimal(MoneyUnit.BTC),
            PaymentMethodId = handler.PaymentMethodId.ToString()
        }.Set(invoice, handler, new CashuPaymentData());
        
        var payment = await _paymentService.AddPayment(paymentData);
        if (markPaid)
        {
            await _invoiceRepository.MarkInvoiceStatus(invoice.Id, InvoiceStatus.Settled);
        }
    }



    private ILightningClient GetStoreLightningClient(StoreData store, BTCPayNetwork network)
    {
        var lightningPmi = PaymentTypes.LN.GetPaymentMethodId(network.CryptoCode);

        var lightningConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lightningPmi,
            _handlers);

        if (lightningConfig == null)
            throw new PaymentMethodUnavailableException("Lightning not configured");

        return lightningConfig.CreateLightningClient(
            network,
            _lightningNetworkOptions.Value,
            _lightningClientFactoryService);
    }

    public async Task AddProofsToDb(IEnumerable<Proof>? proofs, string storeId, string mintUrl)
    {
        if (proofs == null)
        {
            return;
        }
        
        var enumerable = proofs as Proof[] ?? proofs.ToArray();
        
        if (enumerable.Length == 0)
        {
            return;
        }
        
        await using var dbContext = _cashuDbContextFactory.CreateContext();
        
        if (!dbContext.Mints.Any(m => m.Url == mintUrl)) dbContext.Mints.Add(new Mint(mintUrl));

        var dbProofs = StoredProof.FromBatch(enumerable, storeId);
        dbContext.Proofs.AddRange(dbProofs);

        await dbContext.SaveChangesAsync();
    }
    
    
    private CashuPaymentState CompareMeltQuotes(MeltDetails prevMeltState, PostMeltQuoteBolt11Response currentMeltState)
    {
        //Shouldn't happen
        if (prevMeltState.Status == "PAID")
        {
            return CashuPaymentState.Success;
        }
        // paid, should check the invoice state in next
        if (currentMeltState.State == "PAID")
        {
            return CashuPaymentState.Success;
        }
        // if it was pending and now it's not, we should treat it as it never happened. Proofs weren't spent.
        if (prevMeltState.Status == "PENDING")
        {
            if (currentMeltState.State == "UNPAID")
            {
                return CashuPaymentState.Failed;
            }
        }

        if (currentMeltState.State == "PENDING")
        {
            //isn't paid, but it will be 
            return CashuPaymentState.Pending;
        }
        
        //if it's unpaid and it was unpaid let's assume it's pending untill timeout
        if (currentMeltState.State == "UNPAID")
        {
            return prevMeltState.Expiry <= new DateTimeOffset(DateTime.Now) ? CashuPaymentState.Failed : CashuPaymentState.Pending;
        }
        
        return CashuPaymentState.Failed;
    }
    
    private async Task EnsureTokenSpendable(CashuWallet wallet, List<Proof> proofs)
    {
        StateResponseItem.TokenState? tokenState = null;
        try
        {
            tokenState = await wallet.CheckTokenState(proofs);
        }
        catch (Exception ex)
        {
            throw new CashuPaymentException("Failed to check token state", ex);
        }
        switch (tokenState)
        {
            case StateResponseItem.TokenState.SPENT:
                throw new CashuPaymentException("Token already spent");
            case StateResponseItem.TokenState.PENDING:
                throw new CashuPaymentException("Token already pending");
            default:
                return;
        }
    }
    
    public async Task<PollResult> PollFailedMelt(FailedTransaction ftx, StoreData storeData, CashuPaymentMethodHandler handler, CancellationToken cts = default)
    {
        if (ftx.OperationType != OperationType.Melt || ftx.MeltDetails==null)
        {
            throw new InvalidOperationException($"Unexpected operation type: {ftx.OperationType}");
        }
        var lightningClient = GetStoreLightningClient(storeData, handler.Network);
        var lnInvoice = await lightningClient.GetInvoice(ftx.MeltDetails.LightningInvoiceId, cts);
        
        if (lnInvoice.Status == LightningInvoiceStatus.Expired)
        {
            return new PollResult()
            {
                State = CashuPaymentState.Failed
            };
        }

        //If the invoice is paid, we should process the payment, even though if change isn't received.
        if (lnInvoice.Status == LightningInvoiceStatus.Paid)
        {
            var wallet = new CashuWallet(ftx.MintUrl, ftx.Unit);

            try
            {
                var meltQuoteState = await wallet.CheckMeltQuoteState(ftx.MeltDetails.MeltQuoteId, cts);
                var status = CompareMeltQuotes(ftx.MeltDetails, meltQuoteState);
                if (status == CashuPaymentState.Success)
                {   
                    //Change won't be always present
                    if (meltQuoteState.Change == null)
                    {
                        return new PollResult()
                        {
                            State = CashuPaymentState.Success
                        };
                    }
                    var keys = await wallet.GetKeys(meltQuoteState.Change.First().Id);
                    var proofs = CashuUtils.CreateProofs(meltQuoteState.Change, ftx.OutputData.BlindingFactors,
                        ftx.OutputData.Secrets, keys);
                    return new PollResult()
                    {
                        State = CashuPaymentState.Success,
                        ResultProofs = proofs
                    };
                }

                return new PollResult()
                {
                    State = status
                };
            }
            catch (HttpRequestException ex)
            {
                return new PollResult()
                {
                    State = CashuPaymentState.Pending,
                    Error = ex
                };
            }
            catch (Exception ex)
            {
                return new PollResult()
                {
                    State = CashuPaymentState.Unknown,
                    Error = ex
                };
            }
        }

        if (lnInvoice.Status == LightningInvoiceStatus.Expired)
        {
            return new PollResult() { State = CashuPaymentState.Failed };
        }

        return new PollResult() { State = CashuPaymentState.Pending };
        
    }

    public async Task<PollResult> PollFailedSwap(FailedTransaction ftx, StoreData storeData, CancellationToken cts = default)
    {
        if (ftx.OperationType != OperationType.Swap)
        {
            throw new InvalidOperationException($"Unexpected operation type: {ftx.OperationType}");
        }
        var wallet = new CashuWallet(ftx.MintUrl, ftx.Unit);
        try
        {
            //first check if token is spent. if not - don't care
            var tokenState = await wallet.CheckTokenState(ftx.UsedProofs);
            if (tokenState == StateResponseItem.TokenState.UNSPENT)
            {
                return new PollResult()
                {
                    State = CashuPaymentState.Failed,
                };
            }
            
            //try to restore proofs
            var response = await wallet.RestoreProofsFromInputs(ftx.OutputData.BlindedMessages.ToArray(), cts);
            if (response.Signatures.Length == ftx.OutputData.BlindedMessages.Length)
            {
                var keysetId = response.Signatures.First().Id;
                var keys = await wallet.GetKeys(keysetId);
                var proofs = CashuUtils.CreateProofs(response.Signatures, ftx.OutputData.BlindingFactors, ftx.OutputData.Secrets, keys);
                return new PollResult()
                {
                    ResultProofs = proofs,
                    State = CashuPaymentState.Success,
                };
            }

            return new PollResult()
            {
                State = CashuPaymentState.Failed,
                Error = new CashuPluginException("Swap inputs and outputs aren't balanced!")
            };
        }
        catch (Exception ex)
        {
            return new PollResult
            {
                State = CashuPaymentState.Unknown,
                Error = ex,
            };
        }
        
    }

    public class PollResult
    {
        public bool Success => State == CashuPaymentState.Success;
        public CashuPaymentState State { get; set; }
        public Proof[]? ResultProofs { get; set; }
        public Exception? Error { get; set; }
    }
}




