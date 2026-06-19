using System.Threading.Tasks;
using Dignite.Extract.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Extract.HttpApi.Documents;

[Area("extract")]
[Route("api/extract/document-statistics")]
public class DocumentStatisticsController : ExtractController, IDocumentStatisticsAppService
{
    private readonly IDocumentStatisticsAppService _documentStatisticsAppService;

    public DocumentStatisticsController(IDocumentStatisticsAppService documentStatisticsAppService)
    {
        _documentStatisticsAppService = documentStatisticsAppService;
    }

    [HttpGet]
    public virtual Task<DocumentStatisticsDto> GetAsync()
    {
        return _documentStatisticsAppService.GetAsync();
    }
}
