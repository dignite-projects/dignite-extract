using Dignite.Vault.Extract.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace Dignite.Vault.Extract;

public abstract class VaultExtractController : AbpControllerBase
{
    protected VaultExtractController()
    {
        LocalizationResource = typeof(VaultExtractResource);
    }
}
