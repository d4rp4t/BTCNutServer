using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Cashu.Data.enums;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;
public class CashuStatusProvider(StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers)
{
    public async Task<bool> CashuEnabled(string storeId)
    {
        try
        {
            var storeData = await storeRepository.FindStore(storeId);

            var currentPaymentMethodConfig =
                storeData?.GetPaymentMethodConfig<CashuPaymentMethodConfig>(CashuPlugin.CashuPmid, handlers);
            
            if (currentPaymentMethodConfig == null)
                return false;
            
            if (currentPaymentMethodConfig.PaymentModel == CashuPaymentModel.MeltImmediately)
            {
                if (!storeData.IsLightningEnabled("BTC"))
                {
                    return false;
                }
            }
            
            var excludeFilters = storeData.GetStoreBlob().GetExcludedPaymentMethods();
            var enabled = !excludeFilters.Match(CashuPlugin.CashuPmid);

            return enabled;
        }
        catch
        {
            return false;
        }
    }
}