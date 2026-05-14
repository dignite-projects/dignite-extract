using Dignite.Paperbase;
using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Paperbase.Contracts;

[DependsOn(
    typeof(AbpVirtualFileSystemModule),
    typeof(PaperbaseInstallerModule)
    )]
public class ContractsInstallerModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<ContractsInstallerModule>();
        });
    }
}
