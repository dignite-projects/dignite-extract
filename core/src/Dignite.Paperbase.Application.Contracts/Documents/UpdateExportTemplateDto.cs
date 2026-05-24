using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

public class UpdateExportTemplateDto
{
    [Required]
    [DynamicStringLength(typeof(ExportTemplateConsts), nameof(ExportTemplateConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    public ExportFormat Format { get; set; } = ExportFormat.Csv;

    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string? DocumentTypeCode { get; set; }

    [Required]
    [MinLength(1)]
    public List<ExportColumnInput> Columns { get; set; } = new();
}
