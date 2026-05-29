using System;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>字段定义列表查询输入（统一 <c>GetListAsync</c>）。匹配当前层、不跨层。</summary>
public class GetFieldDefinitionListInput
{
    /// <summary>目标文档类型不可变 Id（#207：内部按 Id 关联，TypeCode 可重命名故不作引用键）。</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }

    /// <summary>
    /// <c>true</c> 仅返回回收站（已软删除）字段，按 <c>DeletionTime</c> 倒序；
    /// <c>false</c>（默认）返回活跃字段，按 <c>DisplayOrder</c>。两视图互斥。
    /// </summary>
    public bool OnlyDeleted { get; set; }
}
