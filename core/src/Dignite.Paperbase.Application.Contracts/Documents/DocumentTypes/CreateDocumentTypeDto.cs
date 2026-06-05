using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.DocumentTypes;

public class CreateDocumentTypeDto
{
    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string TypeCode { get; set; } = default!;

    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;

    /// <summary>可选分类辅助说明（#262）：仅帮助 AI 识别此类型，不参与文档内容二次加工。</summary>
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxDescriptionLength))]
    public string? Description { get; set; }

    [Range(0d, 1d)]
    public double ConfidenceThreshold { get; set; } = 0.7;

    public int Priority { get; set; }
}
