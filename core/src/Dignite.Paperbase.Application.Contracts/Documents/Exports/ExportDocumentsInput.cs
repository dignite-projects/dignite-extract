using System;
using System.Collections.Generic;

namespace Dignite.Paperbase.Documents.Exports;

/// <summary>
/// 导出执行入参。<see cref="DocumentIds"/> 非空集合时按勾选 ID 导出（忽略所有筛选字段）；
/// 否则按筛选条件匹配当前层文档（受单次文档数上限约束）。
/// 文档类型不在此指定——模板必然类型绑定（<c>ExportTemplate.DocumentTypeId</c>），导出始终收窄到模板的类型。
/// </summary>
public class ExportDocumentsInput
{
    public Guid TemplateId { get; set; }

    public List<Guid>? DocumentIds { get; set; }

    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    public Guid? CabinetId { get; set; }

    public DateTime? CreationTimeMin { get; set; }

    public DateTime? CreationTimeMax { get; set; }
}
