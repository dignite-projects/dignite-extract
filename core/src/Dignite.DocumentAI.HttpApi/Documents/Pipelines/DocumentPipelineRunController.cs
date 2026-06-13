using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.DocumentAI.Documents.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.DocumentAI.HttpApi.Documents.Pipelines;

// #216 split PipelineRun into an independent aggregate root and added
// IDocumentPipelineRunAppService, but this handwritten controller was missed then. host Auto API only
// covers DocumentAIHostModule.Assembly (see DocumentAIHostModule.ConfigureAutoApiControllers), so
// AppServices in the Application assembly are exposed only through explicit HttpApi controllers. Without
// this forwarding layer, frontend calls to /api/document-ai/document-pipeline-runs hit a 404 with null
// body and break the document detail page forkJoin.
[Area("document-ai")]
[Route("api/document-ai/document-pipeline-runs")]
public class DocumentPipelineRunController : DocumentAIController, IDocumentPipelineRunAppService
{
    private readonly IDocumentPipelineRunAppService _documentPipelineRunAppService;

    public DocumentPipelineRunController(IDocumentPipelineRunAppService documentPipelineRunAppService)
    {
        _documentPipelineRunAppService = documentPipelineRunAppService;
    }

    // GET /api/document-ai/document-pipeline-runs?documentId=...
    // A single Guid simple parameter on GET binds from query string by default, matching the frontend
    // proxy (params: { documentId }).
    [HttpGet]
    public virtual Task<List<DocumentPipelineRunDto>> GetListAsync(Guid documentId)
    {
        return _documentPipelineRunAppService.GetListAsync(documentId);
    }
}
