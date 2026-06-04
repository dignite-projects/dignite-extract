using System;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.Fields;

public class CreateFieldDefinitionDto
{
    /// <summary>父文档类型不可变 Id（#207：建立 FieldDefinition.DocumentTypeId FK，按 Id 而非可重命名的 TypeCode 绑定）。</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    /// <summary>抽取指令——<b>选填</b>。留空时 LLM 仅靠 <see cref="Name"/> + <see cref="DataType"/> 推断该抽什么。</summary>
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxPromptLength))]
    public string? Prompt { get; set; }

    public FieldDataType DataType { get; set; } = FieldDataType.String;

    public int DisplayOrder { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>是否允许多值（#212）——仅 <see cref="FieldDataType.String"/> 字段可为 true，非 String 强行开多值由实体层 loud fail。</summary>
    public bool AllowMultiple { get; set; }
}
