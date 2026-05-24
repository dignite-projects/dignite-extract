using System;
using Volo.Abp.EventBus;

namespace Dignite.Paperbase.Abstractions.Documents;

/// <summary>
/// 文档上传完成（落库 + Blob 落盘）后发布；处于通道流水线起点。
/// 薄载荷：下游通过 REST / MCP 回拉详细数据。不受 Ready 闸门约束。
/// <para>
/// 不变契约（issue #188）：所有属性 <c>init</c>-only——ETO 是事件载荷，发布后不可变；
/// <see cref="EventTime"/> 标 <c>required</c>，编译期强制对象初始化器填值，杜绝 default(DateTime) 风险。
/// </para>
/// </summary>
[EventName("Paperbase.Document.Uploaded")]
public class DocumentUploadedEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// 事件发生时间——Paperbase 在 publish 时填入 <see cref="Volo.Abp.Timing.IClock.Now"/>。
    /// 下游消费方按 <c>(DocumentId, EventType, EventTime)</c> 做幂等：
    /// 同一 key 下若已处理过更晚的 EventTime，则丢弃。
    /// 配合 ABP 内置 transactional outbox 的 at-least-once 投递使用。
    /// </summary>
    public required DateTime EventTime { get; init; }

    public string? FileName { get; init; }

    public long FileSize { get; init; }

    public string? ContentType { get; init; }
}
