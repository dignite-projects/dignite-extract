using Dignite.Extract.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace Dignite.Extract;

public abstract class ExtractController : AbpControllerBase
{
    protected ExtractController()
    {
        LocalizationResource = typeof(ExtractResource);
    }
}
