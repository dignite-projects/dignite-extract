using Dignite.Vault.Extract.Abstractions;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(AbpDddDomainModule),
    typeof(AbpBlobStoringModule),
    typeof(VaultExtractAbstractionsModule),
    typeof(VaultExtractDomainSharedModule)
)]
public class VaultExtractDomainModule : AbpModule
{
}
