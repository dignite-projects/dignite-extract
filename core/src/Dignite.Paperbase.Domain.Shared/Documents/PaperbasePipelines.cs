using System.Collections.Generic;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 核心层定义的流水线标识常量。
/// 业务模块可注册自定义 PipelineCode（建议命名前缀 "{moduleCode}."），
/// 但不会被计入生命周期派生。
/// </summary>
public static class PaperbasePipelines
{
    /// <summary>文本提取（OCR 或原生提取）。关键流水线。</summary>
    public const string TextExtraction = "text-extraction";

    /// <summary>文档分类（规则匹配 / AI）。关键流水线。</summary>
    public const string Classification = "classification";

    /// <summary>文本分块 + 向量化。非关键流水线，失败降级为全文检索。</summary>
    public const string Embedding = "embedding";

    /// <summary>
    /// Issue #115 L2: 关系发现（基于业务模块 <c>IDocumentIdentifierProvider</c> fan-out）。
    /// 非关键流水线——失败不影响 Document 生命周期；orphan 文档（无业务模块认领）会自然短路成 0 关系。
    /// </summary>
    public const string RelationDiscovery = "relation-discovery";

    /// <summary>生命周期派生时视为"关键"的流水线集合。</summary>
    public static readonly IReadOnlyCollection<string> KeyPipelines = new[]
    {
        TextExtraction,
        Classification
    };

    /// <summary>
    /// 用户可手动重试的流水线集合。
    /// Embedding 虽然不计入 <see cref="KeyPipelines"/>（不影响生命周期派生），
    /// 但用户可在它失败时主动重跑——所以这里包含全部三条核心流水线。
    /// 业务模块自定义的流水线不通过此 API 暴露重试。
    /// </summary>
    public static readonly IReadOnlyCollection<string> RetryablePipelines = new[]
    {
        TextExtraction,
        Classification,
        Embedding
    };
}
