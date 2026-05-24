using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

public class ExportColumnInput
{
    public ExportColumnSourceKind SourceKind { get; set; }

    [Required]
    [DynamicStringLength(typeof(ExportTemplateConsts), nameof(ExportTemplateConsts.MaxColumnKeyLength))]
    public string Key { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(ExportTemplateConsts), nameof(ExportTemplateConsts.MaxColumnNameLength))]
    public string ColumnName { get; set; } = default!;

    public int Order { get; set; }
}
