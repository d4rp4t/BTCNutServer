using System;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Cashu.CashuAbstractions;

public class StatefulWalletFactory
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly LightningClientFactoryService _lightningClientFactoryService;
    private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
    private readonly CashuDbContextFactory _cashuDbContextFactory;
    private readonly MintManager _mintManager;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<StatefulWalletFactory> _logger;

    public StatefulWalletFactory(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        LightningClientFactoryService lightningClientFactoryService,
        IOptions<LightningNetworkOptions> lightningNetworkOptions,
        CashuDbContextFactory cashuDbContextFactory,
        MintManager mintManager,
        BTCPayNetworkProvider networkProvider,
        ILogger<StatefulWalletFactory> logger
    )
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _lightningClientFactoryService = lightningClientFactoryService;
        _lightningNetworkOptions = lightningNetworkOptions;
        _cashuDbContextFactory = cashuDbContextFactory;
        _mintManager = mintManager;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    public async Task<StatefulWallet> CreateAsync(
        string storeId,
        string mintUrl,
        string unit = "sat"
    )
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store == null)
        {
            throw new ArgumentException($"Store with ID {storeId} not found.");
        }

        ILightningClient? lightningClient = null;
        try
        {
            // Assuming BTC network for Lightning operations as per standard BTCPay setup
            var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
            if (network != null)
            {
                lightningClient = GetStoreLightningClient(store, network);
            }
        }
        catch (Exception ex)
        {
            // Log warning but proceed. Wallet might be used for operations not requiring Lightning (e.g. Swap)
            _logger.LogDebug("(Cashu) Could not get Lightning Client for store {StoreId}: {Reason}", storeId, ex.Message);
        }

        if (lightningClient != null)
        {
            return new StatefulWallet(
                lightningClient,
                mintUrl,
                unit,
                _cashuDbContextFactory,
                _mintManager,
                storeId
            );
        }

        return new StatefulWallet(mintUrl, unit, _cashuDbContextFactory, _mintManager, storeId);
    }

    private ILightningClient? GetStoreLightningClient(StoreData store, BTCPayNetwork network)
    {
        var lightningPmi = PaymentTypes.LN.GetPaymentMethodId(network.CryptoCode);

        var lightningConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lightningPmi,
            _handlers
        );

        if (lightningConfig == null)
        {
            return null;
        }

        return lightningConfig.CreateLightningClient(
            network,
            _lightningNetworkOptions.Value,
            _lightningClientFactoryService
        );
    }
}
