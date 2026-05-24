using System;
using System.Collections.Generic;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 可导出的系统通用字段白名单（<see cref="ExportColumnSourceKind.System"/> 列的合法 Key 集合）。
/// 对应 <c>Document</c> 顶层 typed column / <c>FileOrigin</c> 字段；取值逻辑在 Application 层导出引擎内 dispatch。
/// <para>
/// 故意排除 <c>Markdown</c>（整段正文，不适合塞进 CSV / XLSX 单元格——下游要正文走 REST 回拉）
/// 与 <c>ClassificationReason</c>（AI 决策解释，非文档数据）。
/// </para>
/// </summary>
public static class ExportSystemFields
{
    public const string Id = "Id";
    public const string Title = "Title";
    public const string DocumentTypeCode = "DocumentTypeCode";
    public const string LifecycleStatus = "LifecycleStatus";
    public const string ReviewStatus = "ReviewStatus";
    public const string Language = "Language";
    public const string OcrConfidence = "OcrConfidence";
    public const string ClassificationConfidence = "ClassificationConfidence";
    public const string CreationTime = "CreationTime";
    public const string OriginalFileName = "OriginalFileName";
    public const string ContentType = "ContentType";
    public const string FileSize = "FileSize";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Id,
        Title,
        DocumentTypeCode,
        LifecycleStatus,
        ReviewStatus,
        Language,
        OcrConfidence,
        ClassificationConfidence,
        CreationTime,
        OriginalFileName,
        ContentType,
        FileSize
    };
}
