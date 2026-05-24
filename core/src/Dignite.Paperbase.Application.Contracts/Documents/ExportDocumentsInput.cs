using System;
using System.Collections.Generic;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出执行入参。<see cref="DocumentIds"/> 非空集合时按勾选 ID 导出（忽略下方筛选条件）；
/// 否则按筛选条件导出当前层匹配文档（受单次文档数上限约束）。
/// </summary>
public class ExportDocumentsInput
{
    public Guid TemplateId { get; set; }

    public List<Guid>? DocumentIds { get; set; }

    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    [DynamicStringLength(typeof(DocumentConsts), nameof(DocumentConsts.MaxDocumentTypeCodeLength))]
    public string? DocumentTypeCode { get; set; }

    [DynamicStringLength(typeof(DocumentConsts), nameof(DocumentConsts.MaxSearchKeywordLength))]
    public string? Keyword { get; set; }
}
