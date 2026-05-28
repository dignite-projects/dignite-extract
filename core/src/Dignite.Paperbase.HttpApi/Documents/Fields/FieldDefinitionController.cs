using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Fields;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Paperbase.HttpApi.Documents.Fields;

[Area("paperbase")]
[Route("api/paperbase/field-definitions")]
public class FieldDefinitionController : PaperbaseController, IFieldDefinitionAppService
{
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;

    public FieldDefinitionController(IFieldDefinitionAppService fieldDefinitionAppService)
    {
        _fieldDefinitionAppService = fieldDefinitionAppService;
    }

    [HttpGet]
    public virtual Task<List<FieldDefinitionDto>> GetByDocumentTypeAsync([FromQuery] string documentTypeCode)
    {
        return _fieldDefinitionAppService.GetByDocumentTypeAsync(documentTypeCode);
    }

    [HttpGet("deleted")]
    public virtual Task<List<FieldDefinitionDto>> GetDeletedByDocumentTypeAsync([FromQuery] string documentTypeCode)
    {
        return _fieldDefinitionAppService.GetDeletedByDocumentTypeAsync(documentTypeCode);
    }

    [HttpPost]
    public virtual Task<FieldDefinitionDto> CreateAsync([FromBody] CreateFieldDefinitionDto input)
    {
        return _fieldDefinitionAppService.CreateAsync(input);
    }

    [HttpPut("{id}")]
    public virtual Task<FieldDefinitionDto> UpdateAsync(Guid id, [FromBody] UpdateFieldDefinitionDto input)
    {
        return _fieldDefinitionAppService.UpdateAsync(id, input);
    }

    [HttpDelete("{id}")]
    public virtual Task DeleteAsync(Guid id)
    {
        return _fieldDefinitionAppService.DeleteAsync(id);
    }

    [HttpPost("{id}/restore")]
    public virtual Task<FieldDefinitionDto> RestoreAsync(Guid id)
    {
        return _fieldDefinitionAppService.RestoreAsync(id);
    }
}
