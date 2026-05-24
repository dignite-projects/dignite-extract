using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出查询投影——只取导出列可能用到的字段，<strong>排除 Markdown</strong>（大 OCR/正文载荷）。
/// 投影到非实体类型时 EF 自动不 SELECT 未引用列、也不进 change tracker，
/// 避免为上万文档把 Markdown 拉进内存。
/// </summary>
internal sealed class ExportProjection
{
    public Guid Id { get; init; }
    public string? Title { get; init; }
    public string? DocumentTypeCode { get; init; }
    public DocumentLifecycleStatus LifecycleStatus { get; init; }
    public DocumentReviewStatus ReviewStatus { get; init; }
    public string? Language { get; init; }
    public double? OcrConfidence { get; init; }
    public double ClassificationConfidence { get; init; }
    public DateTime CreationTime { get; init; }
    public string? OriginalFileName { get; init; }
    public string? ContentType { get; init; }
    public long FileSize { get; init; }
    public Dictionary<string, JsonElement>? ExtractedFields { get; init; }
}
