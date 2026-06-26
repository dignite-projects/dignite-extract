using Microsoft.Extensions.Localization;
using Dignite.Vault.Extract.Host.Localization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Ui.Branding;

namespace Dignite.Vault.Extract.Host;

[Dependency(ReplaceServices = true)]
public class VaultExtractHostBrandingProvider : DefaultBrandingProvider
{
    private readonly IStringLocalizer<VaultExtractHostResource> _localizer;

    public VaultExtractHostBrandingProvider(IStringLocalizer<VaultExtractHostResource> localizer)
    {
        _localizer = localizer;
    }

    public override string AppName => _localizer["AppName"];
}
