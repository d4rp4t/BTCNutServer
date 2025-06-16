using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Controllers;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;
public class CashuPaymentMethodHandler(
    BTCPayNetworkProvider networkProvider,
    IServiceProvider serviceProvider,
    LinkGenerator linkGenerator)
    : IPaymentMethodHandler, IHasNetwork
{
    private readonly BTCPayNetwork _network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
    public PaymentMethodId PaymentMethodId => CashuPlugin.CashuPmid;
    
    public BTCPayNetwork Network => _network;
    
    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var handlers = serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var lightningHandler = (LightningLikePaymentHandler)handlers[PaymentTypes.LN.GetPaymentMethodId(_network.CryptoCode)];
        var store = context.Store;
        var lnPmi = PaymentTypes.LN.GetPaymentMethodId(_network.CryptoCode);
        
        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[this.PaymentMethodId]) is not CashuPaymentMethodConfig cashuConfig)
        {
            throw new PaymentMethodUnavailableException($"Cashu payment method not configured");
        }
        
        var invoice = context.InvoiceEntity;
        ;
        var paymentPath =  $"{invoice.ServerUrl.WithoutEndingSlash()}{linkGenerator.GetPathByAction(nameof(CashuController.PayByPaymentRequest), "Cashu")}";
        
        context.Prompt.PaymentMethodFee = (Money.Satoshis(cashuConfig.FeeConfing.CustomerFeeAdvance).ToDecimal(MoneyUnit.BTC));
        
        var due = Money.Coins(context.Prompt.Calculate().Due);
        var paymentRequest =
            CashuUtils.CreatePaymentRequest(due, invoice.Id, paymentPath, cashuConfig.TrustedMintsUrls);
         context.Prompt.Destination = paymentRequest;
         
        if (cashuConfig.PaymentModel == CashuPaymentModel.MeltImmediately)
        {
            var lnConfig = lightningHandler.ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[lnPmi]);
            if (!store.IsLightningEnabled(_network.CryptoCode))
            {
                throw new PaymentMethodUnavailableException("Melting tokens requires a lightning node to be configured for the store.");
            }
            var preferOnion = Uri.TryCreate(context.InvoiceEntity.ServerUrl, UriKind.Absolute, out var u) && u.IsOnion();
            var nodeInfo = (await lightningHandler.GetNodeInfo(lnConfig, context.Logs, preferOnion)).FirstOrDefault();
        }
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Divisibility = 8;
        // context.Prompt.RateDivisibility = 0;
        return Task.CompletedTask;
    }

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<CashuPaymentMethodDetails>(Serializer);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<CashuPaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(CashuPaymentMethodHandler)}");
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<CashuPaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(CashuPaymentMethodHandler)}");
    }

    public void StripDetailsForNonOwner(object details)
    {
    }
}

public class CashuPaymentData
{ 
    // for now let's keep it as simple as possible. 
}



public class CashuPaymentMethodDetails
{
   public CashuPaymentModel PaymentModel { get; set; }
   public List<string> TrustedMintsUrls { get; set; }
}