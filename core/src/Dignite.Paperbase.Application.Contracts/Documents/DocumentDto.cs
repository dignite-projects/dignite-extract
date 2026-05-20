using System;
using System.Collections.Generic;
using Dignite.Paperbase.Documents;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents;

public class DocumentDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string OriginalFileBlobName { get; set; } = default!;
    public SourceType SourceType { get; set; }
    public FileOriginDto FileOrigin { get; set; } = default!;
    public string? DocumentTypeCode { get; set; }
    public DocumentLifecycleStatus LifecycleStatus { get; set; }
    public DocumentReviewStatus ReviewStatus { get; set; }
    public double ClassificationConfidence { get; set; }
    public string? ClassificationReason { get; set; }
    public DocumentOcrMetadataDto? OcrMetadata { get; set; }

    /// <summary>
    /// 展示标题（文本提取流水线 Run 成功后写入）。
    /// 迁移之前的历史文档可能为 null，UI 需回退到 <see cref="FileOriginDto.OriginalFileName"/>。
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 文档结构化 Markdown 内容（文本提取流水线 Run 成功后写入）。
    /// 前端可直接渲染；需要纯文本时由前端 strip 或后端通过 <c>MarkdownStripper.Strip</c> 投影。
    /// </summary>
    public string? Markdown { get; set; }

    public DateTime CreationTime { get; set; }
    public IList<DocumentPipelineRunDto> PipelineRuns { get; set; } = new List<DocumentPipelineRunDto>();
}
