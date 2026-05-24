using System;
using Volo.Abp.EventBus;

namespace Dignite.Paperbase.Abstractions.Documents;

/// <summary>
/// 文本提取（OCR 或数字版抽取）完成后发布。
/// 携带 OCR 置信度供下游决策；该值是 informational 指标，不门控任何 Paperbase 阶段事件。
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
    /// OCR 置信度（0.0 - 1.0）。仅 OCR 路径有值（<see cref="UsedOcr"/> = true）；
    /// 数字版抽取无 OCR 概念，此值为 <c>null</c>。下游应当依赖 <see cref="UsedOcr"/>
    /// 区分路径，而非把 null 当 1.0 处理。
    /// </summary>
    public double? OcrConfidence { get; init; }

    /// <summary>
    /// 是否实际走了 OCR 路径（true = 图像 OCR；false = 数字版直接抽取）。
    /// </summary>
    public bool UsedOcr { get; init; }
}
