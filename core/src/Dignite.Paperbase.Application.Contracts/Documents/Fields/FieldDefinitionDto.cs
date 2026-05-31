using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents.Fields;

public class FieldDefinitionDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }

    /// <summary>所属文档类型不可变 Id（#207：内部稳定句柄，TypeCode 由 admin 可重命名故不作引用键）。</summary>
    public Guid DocumentTypeId { get; set; }
    public string Name { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Prompt { get; set; } = default!;
    public FieldDataType DataType { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>是否允许多值（#212）——仅 <see cref="FieldDataType.String"/> 字段为 true 时该字段出口渲染为 JSON 数组。</summary>
    public bool AllowMultiple { get; set; }
}
