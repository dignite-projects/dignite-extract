using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes.Packs;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Vault.Extract.HttpApi.Documents.DocumentTypes.Packs;

[Area("vault-extract")]
[Route("api/vault-extract/document-type-packs")]
public class DocumentTypePackController : VaultExtractController, IDocumentTypePackAppService
{
    private readonly IDocumentTypePackAppService _appService;

    public DocumentTypePackController(IDocumentTypePackAppService appService)
    {
        _appService = appService;
    }

    [HttpGet("{id}")]
    public virtual Task<DocumentTypePackDto> ExportAsync(Guid id)
    {
        return _appService.ExportAsync(id);
    }

    [HttpGet]
    public virtual Task<List<DocumentTypePackDto>> ExportAllAsync()
    {
        return _appService.ExportAllAsync();
    }

    [HttpPost("import")]
    public virtual Task<DocumentTypePackImportResultDto> ImportAsync([FromBody] ImportDocumentTypePacksInput input)
    {
        return _appService.ImportAsync(input);
    }
}
