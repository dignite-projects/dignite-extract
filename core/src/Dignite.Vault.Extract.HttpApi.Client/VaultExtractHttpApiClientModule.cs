using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Http.Client;
using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(VaultExtractApplicationContractsModule),
    typeof(AbpHttpClientModule))]
public class VaultExtractHttpApiClientModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddHttpClientProxies(
            typeof(VaultExtractApplicationContractsModule).Assembly,
            VaultExtractRemoteServiceConsts.RemoteServiceName
        );

        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<VaultExtractHttpApiClientModule>();
        });

    }
}
