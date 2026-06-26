using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(AbpVirtualFileSystemModule)
    )]
public class VaultExtractInstallerModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<VaultExtractInstallerModule>();
        });
    }
}
