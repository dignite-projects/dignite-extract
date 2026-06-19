using Volo.Abp.Modularity;

namespace Dignite.Extract;

[DependsOn(
    typeof(ExtractApplicationModule),
    typeof(ExtractDomainTestModule)
    )]
public class ExtractApplicationTestModule : AbpModule
{
}
