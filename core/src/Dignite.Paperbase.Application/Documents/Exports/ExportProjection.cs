using System;
using System.Collections.Generic;
using Dignite.Paperbase.Documents.Fields;

namespace Dignite.Paperbase.Documents.Exports;

/// <summary>
/// 导出查询投影——只取导出列可能用到的字段，<strong>排除 Markdown</strong>（大 OCR/正文载荷）。
/// 投影到非实体类型时 EF 自动不 SELECT 未引用列、也不进 change tracker，
/// 避免为上万文档把 Markdown 拉进内存。
/// <para>
/// <see cref="ExtractedFields"/> 是 <see cref="DocumentExtractedField"/> child 行的 typed 投影
/// （Issue #206）——在单条投影查询里随文档一并 SELECT（相关子查询 / JOIN，非逐文档 N+1）。
/// </para>
/// </summary>
internal sealed class ExportProjection
{
    public Guid Id { get; init; }
    public string? Title { get; init; }
    public string? DocumentTypeCode { get; init; }
    public DocumentLifecycleStatus LifecycleStatus { get; init; }
    public DocumentReviewStatus ReviewStatus { get; init; }
    public string? Language { get; init; }
    public double ClassificationConfidence { get; init; }
    public DateTime CreationTime { get; init; }
    public string? OriginalFileName { get; init; }
    public string? ContentType { get; init; }
    public long FileSize { get; init; }
    public List<ExtractedFieldProjection> ExtractedFields { get; init; } = new();
}

/// <summary>单个类型绑定字段值的 typed 投影——导出按 <see cref="DataType"/> 渲染对应列的单元格字符串。</summary>
internal sealed class ExtractedFieldProjection
{
    public string Name { get; init; } = default!;
    public FieldDataType DataType { get; init; }
    public string? StringValue { get; init; }
    public bool? BooleanValue { get; init; }
    public long? IntegerValue { get; init; }
    public decimal? DecimalValue { get; init; }
    public DateOnly? DateValue { get; init; }
    public DateTime? DateTimeValue { get; init; }
}
