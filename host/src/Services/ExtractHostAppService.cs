using Volo.Abp.Application.Services;
using Dignite.Extract.Host.Localization;

namespace Dignite.Extract.Host.Services;

/* Inherit your application services from this class. */
public abstract class ExtractHostAppService : ApplicationService
{
    protected ExtractHostAppService()
    {
        LocalizationResource = typeof(ExtractHostResource);
    }
}
