namespace Dignite.Paperbase.Documents.Pipelines;

public enum PipelineRunStatus
{
    /// <summary>已创建尚未开始（入队但尚未拉起）</summary>
    Pending = 10,

    /// <summary>正在执行</summary>
    Running = 20,

    /// <summary>成功完成</summary>
    Succeeded = 30,

    /// <summary>失败（重试上限内仍失败才最终进入此状态）</summary>
    Failed = 90,

    /// <summary>跳过（前置条件未满足，或租户 Feature 关闭）</summary>
    Skipped = 95
}
