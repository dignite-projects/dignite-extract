using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.DocumentTypes;

public class UpdateDocumentTypeDto
{
    /// <summary>类型机器码（#207 起允许重命名；regex 白名单由实体校验，同层 (TenantId, TypeCode) 唯一性由 AppService 校验）。</summary>
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
    public double ConfidenceThreshold { get; set; }

    public int Priority { get; set; }
}
