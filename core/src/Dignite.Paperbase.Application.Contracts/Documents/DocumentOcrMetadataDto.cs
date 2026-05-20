using Dignite.Paperbase.Abstractions.TextExtraction;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// <see cref="DocumentOcrMetadata"/> 的出口投影。Mapperly 自动 inline 嵌套映射。
/// </summary>
public class DocumentOcrMetadataDto
{
    public string? RequestedProfileCode { get; set; }
    public string? EffectiveProfileCode { get; set; }
    public string? ResolutionReason { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderModelName { get; set; }
    public string? ProviderVersion { get; set; }
    public OcrQualitySignalSnapshot? QualitySignals { get; set; }
}
