using System;
using Volo.Abp.EventBus;

namespace Dignite.Paperbase.Abstractions.Documents;

/// <summary>
/// 文档被彻底删除（物理删除）时发布。
/// 与 <see cref="DocumentDeletedEto"/>（软删除信号）的区别：
/// 软删除可被恢复，业务模块应将自身数据置为可恢复的归档状态；
/// 彻底删除不可恢复，业务模块应物理删除从该文档派生的数据（合同、抽取字段等）。
/// <para>
/// 不变契约（issue #188）：所有属性 <c>init</c>-only；<see cref="EventTime"/> 标 <c>required</c>。
/// </para>
/// </summary>
[EventName("Paperbase.Document.PermanentlyDeleted")]
public class DocumentPermanentlyDeletedEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// 事件发生时间——Paperbase 在 publish 时填入 <see cref="Volo.Abp.Timing.IClock.Now"/>。
    /// 下游消费方按 <c>(DocumentId, EventType, EventTime)</c> 做幂等（at-least-once 投递）。
    /// </summary>
    public required DateTime EventTime { get; init; }
}
