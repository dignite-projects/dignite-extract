using System;
using System.Collections.Generic;
using Dignite.Paperbase.Documents;
using Volo.Abp.ObjectExtending;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// 各 pipeline 的通用执行记录。Pipeline 专属输出统一落 <see cref="ExtensibleObject.ExtraProperties"/>
/// （key 见 <see cref="PipelineRunExtraPropertyNames"/>）；面向客户端 / Angular 时，对每一个被
/// 显式提升的 key 在 DTO 上配一个强类型属性（当前仅 <see cref="Candidates"/>），
/// 让 abp generate-proxy 把类型一路同步到 TS，前端无需按字符串 key cast。
/// </summary>
public class DocumentPipelineRunDto : ExtensibleObject
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string PipelineCode { get; set; } = default!;
    public PipelineRunStatus Status { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>
    /// 分类流水线 LLM 给出的 top-K 备选类型，仅在低置信度路径
    /// （<c>DocumentPipelineRunManager.CompleteClassificationWithLowConfidenceAsync</c>）写入。
    /// 物理存储：<see cref="ExtensibleObject.ExtraProperties"/>[<see cref="PipelineRunExtraPropertyNames.ClassificationCandidates"/>]
    /// JSON array；服务端的 <c>DocumentPipelineRunToDocumentPipelineRunDtoMapper</c>
    /// 在 mapping 时从 ExtraProperties 反序列化填入此属性；下游 HTTP/STJ 反序列化时
    /// 由 STJ 直接 set。无候选时为 <see langword="null"/>（不约定空数组）。
    /// </summary>
    public IReadOnlyList<PipelineRunCandidate>? Candidates { get; set; }
}
