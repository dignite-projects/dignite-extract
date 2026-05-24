using System;
using System.Collections.Generic;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents;

public class ExportTemplateDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = default!;
    public ExportFormat Format { get; set; }
    public string? DocumentTypeCode { get; set; }
    public List<ExportColumnDto> Columns { get; set; } = new();
}
