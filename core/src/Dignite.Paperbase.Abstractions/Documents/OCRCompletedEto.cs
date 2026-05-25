using System;
using Volo.Abp.EventBus;

namespace Dignite.Paperbase.Abstractions.Documents;

/// <summary>
/// 文本提取（OCR 或数字版抽取）完成后发布。
/// <see cref="UsedOcr"/> 标记走的是图像 OCR 还是数字版直接抽取；下游通过 REST 回拉 Markdown。
/// <para>
/// 不变契约（issue #188）：所有属性 <c>init</c>-only；<see cref="EventTime"/> 标 <c>required</c>。
/// </para>
/// </summary>
[EventName("Paperbase.Document.OCRCompleted")]
public class OCRCompletedEto
{
    public string Version { get; init; } = "1.0";

    public Guid DocumentId { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>
    /// 事件发生时间——Paperbase 在 publish 时填入 <see cref="Volo.Abp.Timing.IClock.Now"/>。
    /// 下游消费方按 <c>(DocumentId, EventType, EventTime)</c> 做幂等（at-least-once 投递）。
    /// </summary>
    public required DateTime EventTime { get; init; }

    /// <summary>
    /// 是否实际走了 OCR 路径（true = 图像 OCR；false = 数字版直接抽取）。
    /// </summary>
    public bool UsedOcr { get; init; }
}
