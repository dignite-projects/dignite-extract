namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// 分类流水线产出的 top-K 候选项 JSON payload schema。
/// 物理存储位置：<see cref="DocumentPipelineRun.ExtraProperties"/>[<see cref="PipelineRunExtraPropertyNames.ClassificationCandidates"/>]。
/// 通过 <see cref="DocumentPipelineRunDto.Candidates"/> 强类型暴露给 Angular，避免前端按 key 字符串 cast。
/// </summary>
public record PipelineRunCandidate(string TypeCode, double ConfidenceScore);
