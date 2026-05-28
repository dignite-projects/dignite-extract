using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.Fields;

public class CreateFieldDefinitionDto
{
    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDocumentTypeCodeLength))]
    public string DocumentTypeCode { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxPromptLength))]
    public string Prompt { get; set; } = default!;

    public FieldDataType DataType { get; set; } = FieldDataType.String;

    public int DisplayOrder { get; set; }

    public bool IsRequired { get; set; }
}
