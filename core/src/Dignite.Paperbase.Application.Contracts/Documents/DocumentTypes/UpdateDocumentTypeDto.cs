using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.DocumentTypes;

public class UpdateDocumentTypeDto
{
    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    [Range(0d, 1d)]
    public double ConfidenceThreshold { get; set; }

    public int Priority { get; set; }
}
