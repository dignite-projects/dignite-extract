using System;

namespace Dignite.DocumentAI.Documents.Fields;

/// <summary>字段定义列表查询输入（统一 <c>GetListAsync</c>）。匹配当前层、不跨层。</summary>
public class GetFieldDefinitionListInput
{
    /// <summary>
    /// 目标文档类型不可变 Id（#207：内部按 Id 关联，TypeCode 可重命名故不作引用键）。
    /// 留空（<c>null</c>）= 不按类型过滤，单次返回当前层全部字段定义——供 MCP <c>list_document_types</c>
    /// 等批量读取方一次取全、内存按 DocumentTypeId 分组，消 per-type N+1 查询。
    /// 权限门与按类型查询完全一致（不放大可见范围——逐类型枚举本就能拿到同一集合）。
    /// </summary>
    public Guid? DocumentTypeId { get; set; }

    /// <summary>
    /// <c>true</c> 仅返回回收站（已软删除）字段，按 <c>DeletionTime</c> 倒序；
    /// <c>false</c>（默认）返回活跃字段，按 <c>DisplayOrder</c>（批量时先按 <c>DocumentTypeId</c>）。两视图互斥。
    /// </summary>
    public bool OnlyDeleted { get; set; }
}
