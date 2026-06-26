using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(VaultExtractApplicationModule),
    typeof(VaultExtractDomainTestModule)
    )]
public class VaultExtractApplicationTestModule : AbpModule
{
}
