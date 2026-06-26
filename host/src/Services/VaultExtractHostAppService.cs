using Volo.Abp.Application.Services;
using Dignite.Vault.Extract.Host.Localization;

namespace Dignite.Vault.Extract.Host.Services;

/* Inherit your application services from this class. */
public abstract class VaultExtractHostAppService : ApplicationService
{
    protected VaultExtractHostAppService()
    {
        LocalizationResource = typeof(VaultExtractHostResource);
    }
}
