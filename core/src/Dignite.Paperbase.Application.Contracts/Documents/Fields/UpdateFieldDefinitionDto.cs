using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.Fields;

public class UpdateFieldDefinitionDto
{
    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxPromptLength))]
    public string Prompt { get; set; } = default!;

    public FieldDataType DataType { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsRequired { get; set; }
}
