using Microsoft.Extensions.Localization;
using Dignite.Extract.Host.Localization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Ui.Branding;

namespace Dignite.Extract.Host;

[Dependency(ReplaceServices = true)]
public class ExtractHostBrandingProvider : DefaultBrandingProvider
{
    private readonly IStringLocalizer<ExtractHostResource> _localizer;

    public ExtractHostBrandingProvider(IStringLocalizer<ExtractHostResource> localizer)
    {
        _localizer = localizer;
    }

    public override string AppName => _localizer["AppName"];
}
