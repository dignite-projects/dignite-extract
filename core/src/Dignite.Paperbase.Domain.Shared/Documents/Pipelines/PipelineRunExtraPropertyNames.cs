namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// <see cref="DocumentPipelineRun.ExtraProperties"/> 的 key 常量。
/// 每种 pipeline 约定自己使用的 key；业务模块若新增 pipeline，建议前缀 "{moduleCode}." 以避免冲突。
/// </summary>
public static class PipelineRunExtraPropertyNames
{
    /// <summary>
    /// 分类流水线 top-K 候选结果。
    /// 写入时使用 <see cref="PipelineRunCandidate"/> 作为 JSON payload schema；
    /// 读取侧通过 <see cref="DocumentPipelineRunDto.Candidates"/> 强类型暴露。
    /// <para>
    /// 必须是 <c>const</c>：这是 JSON 列的持久化 key 字面量。
    /// 任何运行时改动都会让历史 <c>ExtraProperties["Candidates"]</c> 读不回。
    /// </para>
    /// </summary>
    public const string ClassificationCandidates = "Candidates";
}
