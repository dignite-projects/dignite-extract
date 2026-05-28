namespace Dignite.Paperbase.Documents.DocumentTypes;

/// <summary>
/// 文档分类相关的默认常量。集中维护以避免阈值魔法值散落在多个分类器中。
/// </summary>
public static class ClassificationDefaults
{
    /// <summary>
    /// <c>DocumentType.ConfidenceThreshold</c> 的默认阈值。
    /// 低于此值（且类型未在白名单中）进入 LowConfidence 路径。
    /// </summary>
    public const double DefaultConfidenceThreshold = 0.7;

    /// <summary>
    /// 人工确认（manual classification）写入的固定置信度。
    /// 注意：核心 Domain 项目不依赖 Abstractions，
    /// <c>DocumentPipelineRunManager.CompleteManualClassificationAsync</c> 中以字面量 1.0 出现；
    /// 若变更此常量请同步更新该处。
    /// </summary>
    public const double ManualClassificationConfidence = 1.0;
}
