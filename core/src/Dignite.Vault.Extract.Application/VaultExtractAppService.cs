using Dignite.Vault.Extract.Localization;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract;

public abstract class VaultExtractAppService : ApplicationService
{
    protected VaultExtractAppService()
    {
        LocalizationResource = typeof(VaultExtractResource);
        ObjectMapperContext = typeof(VaultExtractApplicationModule);
    }
}
