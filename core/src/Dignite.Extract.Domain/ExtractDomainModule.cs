using Dignite.Extract.Abstractions;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain;
using Volo.Abp.Modularity;

namespace Dignite.Extract;

[DependsOn(
    typeof(AbpDddDomainModule),
    typeof(AbpBlobStoringModule),
    typeof(ExtractAbstractionsModule),
    typeof(ExtractDomainSharedModule)
)]
public class ExtractDomainModule : AbpModule
{
}
