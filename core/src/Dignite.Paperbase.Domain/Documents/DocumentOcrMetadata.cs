using System.Collections.Generic;
using Dignite.Paperbase.Abstractions.TextExtraction;
using Volo.Abp.Domain.Values;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// Document 上持久化的 OCR 元数据（值对象，写入后不可变）。
/// 由 transport 契约 <see cref="OcrExtractionMetadata"/> 经 <see cref="Document.SetOcrMetadata"/> 映射而来——
/// 两者字段形状相同但职责不同：<see cref="OcrExtractionMetadata"/> 是跨 <c>ITextExtractor</c> 实现边界的对外
/// transport 契约（public-set DTO），本类型是聚合根的封装状态（private-set 值对象）。形状相同 ≠ 同一类型。
/// </summary>
public class DocumentOcrMetadata : ValueObject
{
    /// <summary>请求的 OCR profile code（解析前）。</summary>
    public string? RequestedProfileCode { get; private set; }

    /// <summary>实际生效的 OCR profile code（解析后）。</summary>
    public string? EffectiveProfileCode { get; private set; }

    /// <summary>profile 解析理由（auto resolver 的决策说明）。</summary>
    public string? ResolutionReason { get; private set; }

    /// <summary>OCR provider 名称。</summary>
    public string? ProviderName { get; private set; }

    /// <summary>OCR provider 模型名。</summary>
    public string? ProviderModelName { get; private set; }

    /// <summary>OCR provider 版本。</summary>
    public string? ProviderVersion { get; private set; }

    /// <summary>OCR 质量信号快照（json 附属，调试 / targeted re-OCR 决策依据）。</summary>
    public OcrQualitySignalSnapshot? QualitySignals { get; private set; }

    protected DocumentOcrMetadata()
    {
    }

    public DocumentOcrMetadata(
        string? requestedProfileCode,
        string? effectiveProfileCode,
        string? resolutionReason,
        string? providerName,
        string? providerModelName,
        string? providerVersion,
        OcrQualitySignalSnapshot? qualitySignals)
    {
        RequestedProfileCode = TrimToMax(requestedProfileCode, DocumentConsts.MaxOcrProfileCodeLength);
        EffectiveProfileCode = TrimToMax(effectiveProfileCode, DocumentConsts.MaxOcrProfileCodeLength);
        ResolutionReason = TrimToMax(resolutionReason, DocumentConsts.MaxOcrProfileResolutionReasonLength);
        ProviderName = TrimToMax(providerName, DocumentConsts.MaxOcrProviderNameLength);
        ProviderModelName = TrimToMax(providerModelName, DocumentConsts.MaxOcrProviderModelNameLength);
        ProviderVersion = TrimToMax(providerVersion, DocumentConsts.MaxOcrProviderVersionLength);
        QualitySignals = qualitySignals;
    }

    // 相等性以 profile / provider 标识为准；QualitySignals 是附属 json 快照，不纳入原子值。
    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return RequestedProfileCode ?? string.Empty;
        yield return EffectiveProfileCode ?? string.Empty;
        yield return ResolutionReason ?? string.Empty;
        yield return ProviderName ?? string.Empty;
        yield return ProviderModelName ?? string.Empty;
        yield return ProviderVersion ?? string.Empty;
    }

    private static string? TrimToMax(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
