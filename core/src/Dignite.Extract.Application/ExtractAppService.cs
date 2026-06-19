using Dignite.Extract.Localization;
using Volo.Abp.Application.Services;

namespace Dignite.Extract;

public abstract class ExtractAppService : ApplicationService
{
    protected ExtractAppService()
    {
        LocalizationResource = typeof(ExtractResource);
        ObjectMapperContext = typeof(ExtractApplicationModule);
    }
}
