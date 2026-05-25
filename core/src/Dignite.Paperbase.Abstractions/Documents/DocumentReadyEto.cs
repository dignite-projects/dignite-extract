using System;
using Volo.Abp.EventBus;

namespace Dignite.Paperbase.Abstractions.Documents;

/// <summary>
/// 全流水线完成 + 文档拿到已确认类型后发布——下游消费方的"可信信号"。
/// 生命周期跃迁到 <c>Ready</c> 即隐含通过分类 / 人工审核闸门：
/// <list type="bullet">
///   <item>自动分类置信度 ≥ 类型门槛 → 自动到 Ready 发布</item>
///   <item>分类置信度不足 / 无合适类型 → 文档进待人工审核队列；操作员确认类型后才发布</item>
/// </list>
/// 大多数下游业务消费方应订阅此事件而非早期阶段事件（DocumentUploaded/OCRCompleted/...）。
/// <para>
/// 不变契约（issue #188）：所有属性 <c>init</c>-only；<see cref="EventTime"/> 标 <c>required</c>。
/// </para>
/// </summary>
[EventName("Paperbase.Document.Ready")]
public class DocumentReadyEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// 事件发生时间——Paperbase 在 publish 时填入 <see cref="Volo.Abp.Timing.IClock.Now"/>。
    /// 下游消费方按 <c>(DocumentId, EventType, EventTime)</c> 做幂等（at-least-once 投递）。
    /// </summary>
    public required DateTime EventTime { get; init; }

    public string? DocumentTypeCode { get; init; }
}
