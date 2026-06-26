using Volo.Abp.Application;
using Volo.Abp.Modularity;
using Volo.Abp.Authorization;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(VaultExtractDomainSharedModule),
    typeof(AbpDddApplicationContractsModule),
    typeof(AbpAuthorizationModule)
    )]
public class VaultExtractApplicationContractsModule : AbpModule
{

}
