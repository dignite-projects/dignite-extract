using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.Fields;

public class UpdateFieldDefinitionDto
{
    /// <summary>字段机器名（#207 起允许重命名；regex 白名单由实体校验，同层同类型唯一性由 AppService 校验）。</summary>
    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxPromptLength))]
    public string Prompt { get; set; } = default!;

    public FieldDataType DataType { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>是否允许多值（#212）——仅 <see cref="FieldDataType.String"/> 字段可为 true。multi→single 收窄对已有值字段由 AppService 拒绝。</summary>
    public bool AllowMultiple { get; set; }
}
