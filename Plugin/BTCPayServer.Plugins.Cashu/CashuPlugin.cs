using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Cashu.CashuAbstractions;
using BTCPayServer.Plugins.Cashu.Data;
using BTCPayServer.Plugins.Cashu.Lightning;
using BTCPayServer.Plugins.Cashu.PaymentHandlers;
using BTCPayServer.Plugins.Cashu.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Cashu;

public class CashuPlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = nameof(CashuPlugin) + "Nav";
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" },
        };

    internal static readonly PaymentMethodId CashuPmid = new("CASHU");
    internal static readonly string CashuDisplayName = "Cashu";

    public override void Execute(IServiceCollection services)
    {
        services.AddTransactionLinkProvider(CashuPmid, new CashuTransactionLinkProvider("cashu"));

        services.AddSingleton<CashuPaymentMethodHandler>();
        services.AddSingleton<IPaymentMethodHandler>(provider => provider.GetRequiredService<CashuPaymentMethodHandler>());
        services.AddSingleton(provider =>
            (ICheckoutModelExtension)
                ActivatorUtilities.CreateInstance(provider, typeof(CashuCheckoutModelExtension))
        );
        services.AddDefaultPrettyName(CashuPmid, CashuDisplayName);

        //Cashu Singletons
        services.AddSingleton<MintManager>();
        services.AddSingleton<CashuStatusProvider>();
        services.AddSingleton<CashuPaymentService>();
        services.AddSingleton<RestoreService>();
        services.AddHostedService(s => s.GetRequiredService<RestoreService>());
        services.AddSingleton<StatefulWalletFactory>();
        services.AddSingleton<MintListener>();
        services.AddHostedService(s => s.GetRequiredService<MintListener>());
        services.AddSingleton<ILightningConnectionStringHandler, CashuLightningConnectionStringHandler>();

        //Ui extensions
        services.AddUIExtension("store-wallets-nav", "CashuStoreNav");
        services.AddUIExtension("checkout-payment", "CashuCheckout");
        services.AddUIExtension("ln-payment-method-setup-tab", "LNPaymentMethodSetupTab");

        //Database Services
        services.AddSingleton<CashuDbContextFactory>();
        services.AddDbContext<CashuDbContext>(
            (provider, o) =>
            {
                var factory = provider.GetRequiredService<CashuDbContextFactory>();
                factory.ConfigureBuilder(o);
            }
        );
        services.AddHostedService<MigrationRunner>();

        // i couldn't see debug logs without it
#if DEBUG
        services.Configure<LoggerFilterOptions>(opts =>
            opts.Rules.Add(new LoggerFilterRule(null, "BTCPayServer.Plugins.Cashu", LogLevel.Debug, null)));
#endif

        base.Execute(services);
    }
}
/*                                              
         .-+****%@@@@@@@@@@@@@@@#-                        
         :                       #                        
      .-+%                       @@%-                     
   .:=*                             %                     
   -                                @                     
   =                                @                     
   +                                @                     
   +                                %                     
   .                             #=:.                     
    @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@                      
    @@@ @ @@@@@@@@@  @@@ @  @@@@@@@@                      
   .   @@ @  @@@@@    *@@@  @ @@@                         
   +    @@@@@@@@        @@@@@@@@                          
   +                          @#+-                        
   +                             #                        
   +                             @                        
   +                             @@@@@@@@%=               
   +                                      =               
   +                                      @@@+.           
   +                                          -           
   -                                         -@@+-        
   .-+@                                          *        
      +                                          @        
      -                                          @        
      .-+#                                       #        
         .                                       =        
          ..::                                +-:         
             :=+@                         @#+-            
                -                         .               
                .-++++*******************=:               
*/