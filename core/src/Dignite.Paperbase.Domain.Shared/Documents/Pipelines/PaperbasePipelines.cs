using System.Collections.Generic;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// 核心层定义的流水线标识常量。
/// 业务模块可注册自定义 PipelineCode（建议命名前缀 "{moduleCode}."），
/// 但不会被计入生命周期派生。
/// <para>
/// <see cref="TextExtraction"/> / <see cref="Classification"/> 必须是 <c>const</c>：
/// 它们持久化到 <c>DocumentPipelineRun.PipelineCode</c> 列、跨 JobArgs / ETO 载荷传递，
/// 且用作 <c>DocumentPipelineJobScheduler</c> switch expression 的 constant pattern。
/// 任何运行时 mutate 都会让历史 DB 数据按旧 code 写、新代码按新 code 查，分发逻辑断裂。
/// </para>
/// </summary>
public static class PaperbasePipelines
{
    /// <summary>文本提取（OCR 或原生提取）。关键流水线。</summary>
    public const string TextExtraction = "text-extraction";

    /// <summary>文档分类（规则匹配 / AI）。关键流水线。</summary>
    public const string Classification = "classification";

    /// <summary>生命周期派生时视为"关键"的流水线集合。</summary>
    public static readonly IReadOnlyCollection<string> KeyPipelines = new[]
    {
        TextExtraction,
        Classification
    };

    /// <summary>
    /// 用户可手动重试的流水线集合。
    /// 业务模块自定义的流水线不通过此 API 暴露重试。
    /// </summary>
    public static readonly IReadOnlyCollection<string> RetryablePipelines = new[]
    {
        TextExtraction,
        Classification
    };
}
