using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Controllers;
using BTCPayServer.Plugins.Cashu.Data.enums;
using Microsoft.AspNetCore.Routing;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;

public class CashuPaymentMethodHandler(
    BTCPayNetworkProvider networkProvider,
    LinkGenerator linkGenerator
) : IPaymentMethodHandler, IHasNetwork
{
    private readonly BTCPayNetwork _network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
    public PaymentMethodId PaymentMethodId => CashuPlugin.CashuPmid;
    public BTCPayNetwork Network => _network;
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var store = context.Store;
        if (
            ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[this.PaymentMethodId])
            is not CashuPaymentMethodConfig cashuConfig
        )
        {
            throw new PaymentMethodUnavailableException($"Cashu payment method not configured");
        }
        if (cashuConfig.FeeConfing is null)
        {
            throw new PaymentMethodUnavailableException(
                "Fee config is missing! Check out cashu payment method settings."
            );
        }

        var invoice = context.InvoiceEntity;
        var paymentPath =
            $"{invoice.ServerUrl.WithoutEndingSlash()}{linkGenerator.GetPathByAction(nameof(CashuPaymentController.PayByPaymentRequest), "CashuPayment")}";
        context.Prompt.PaymentMethodFee = Money
            .Satoshis(cashuConfig.FeeConfing.CustomerFeeAdvance)
            .ToDecimal(MoneyUnit.BTC);

        var due = Money.Coins(context.Prompt.Calculate().Due);
        var paymentRequest = CashuUtils.CreatePaymentRequest(
            due,
            invoice.Id,
            paymentPath,
            cashuConfig.PaymentModel == CashuPaymentModel.TrustedMintsOnly
                ? cashuConfig.TrustedMintsUrls :
                null // add mints only if trustedmintsonly
        );
        context.Prompt.Destination = paymentRequest;

        if (cashuConfig.PaymentModel == CashuPaymentModel.HoldWhenTrusted)
        {
            if (!store.IsLightningEnabled(_network.CryptoCode))
            {
                throw new PaymentMethodUnavailableException(
                    "Melting tokens requires a lightning node to be configured for the store."
                );
            }
        }

        var details = new CashuPaymentMethodDetails
        {
            TrustedMintsUrls = cashuConfig.TrustedMintsUrls,
        };
        context.Prompt.Details = JObject.FromObject(details, Serializer);
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Divisibility = 8;

        // context.Prompt.RateDivisibility = 0;
        return Task.CompletedTask;
    }

    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<CashuPaymentMethodDetails>(Serializer);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<CashuPaymentMethodConfig>(Serializer)
            ?? throw new FormatException($"Invalid {nameof(CashuPaymentMethodHandler)}");
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<CashuPaymentData>(Serializer)
            ?? throw new FormatException($"Invalid {nameof(CashuPaymentMethodHandler)}");
    }

    public void StripDetailsForNonOwner(object details) { }
}

public class CashuPaymentData
{
    // for now let's keep it as simple as possible.
}

public class CashuPaymentMethodDetails
{
    public List<string> TrustedMintsUrls { get; set; }
}
