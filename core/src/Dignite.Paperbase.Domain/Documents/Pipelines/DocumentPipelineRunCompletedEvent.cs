using System;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// 单条流水线 Run 结束的本地域事件（成功、失败、跳过均发出）。
/// 在 <see cref="DocumentPipelineRunManager"/> 的 CompleteAsync / FailAsync / SkipAsync 内发布，与状态变更同事务。
/// 典型监听场景：流水线级监控/审计、失败后重试决策、业务模块挂载自定义后续处理。
/// </summary>
public class DocumentPipelineRunCompletedEvent
{
    public Guid DocumentId { get; }
    public string PipelineCode { get; }
    public PipelineRunStatus Status { get; }

    public DocumentPipelineRunCompletedEvent(
        Guid documentId,
        string pipelineCode,
        PipelineRunStatus status)
    {
        DocumentId = documentId;
        PipelineCode = pipelineCode;
        Status = status;
    }
}
