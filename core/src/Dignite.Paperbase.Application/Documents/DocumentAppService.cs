using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.BlobStoring;
using Volo.Abp.Content;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EventBus.Distributed;

namespace Dignite.Paperbase.Documents;

public class DocumentAppService : PaperbaseAppService, IDocumentAppService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IDistributedEventBus _distributedEventBus;

    public DocumentAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IBlobContainer<PaperbaseDocumentContainer> blobContainer,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IDistributedEventBus distributedEventBus)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _blobContainer = blobContainer;
        _pipelineRunManager = pipelineRunManager;
        _pipelineJobScheduler = pipelineJobScheduler;
        _distributedEventBus = distributedEventBus;
    }

    public virtual async Task<DocumentDto> GetAsync(Guid id)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    public virtual async Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);

        // 回收站视图：需要 Restore 权限，且整个查询管道必须在 DataFilter.Disable<ISoftDelete> 作用域内
        if (input.IsDeleted == true)
        {
            await CheckPolicyAsync(PaperbasePermissions.Documents.Restore);
            using (DataFilter.Disable<ISoftDelete>())
            {
                return await ExecuteListQueryAsync(input, onlyDeleted: true);
            }
        }

        return await ExecuteListQueryAsync(input, onlyDeleted: false);
    }

    protected virtual async Task<PagedResultDto<DocumentListItemDto>> ExecuteListQueryAsync(
        GetDocumentListInput input,
        bool onlyDeleted)
    {
        var query = await _documentRepository.GetQueryableAsync();
        query = ApplyFilter(query, input);
        if (onlyDeleted)
        {
            query = query.Where(d => d.IsDeleted);
        }

        var totalCount = await AsyncExecuter.CountAsync(query);

        query = ApplySorting(query, input.Sorting);
        query = query.Skip(input.SkipCount).Take(input.MaxResultCount);

        var documents = await AsyncExecuter.ToListAsync(query);

        return new PagedResultDto<DocumentListItemDto>(
            totalCount,
            ObjectMapper.Map<List<Document>, List<DocumentListItemDto>>(documents));
    }

    [Authorize(PaperbasePermissions.Documents.Upload)]
    public virtual async Task<DocumentDto> UploadAsync(UploadDocumentInput input)
    {
        // 前置检查：当前层至少要有一个 DocumentType（CLAUDE.md "两层文档类型体系" 单层精确匹配）。
        // Host 启动期 seed 入口已删除（HostDocumentTypeDataSeedContributor / DocumentTypeOptions），
        // DocumentType 现在只能通过 IDocumentTypeAppService 运行时创建——所以新部署 / 新租户必须先建类型才能上传。
        // 不做这个 fail-fast 检查的话，上传成功 → 分类候选集为空 → 文档永远卡 PendingReview。
        var hasType = (await _documentTypeRepository.GetByTenantAsync()).Any();
        if (!hasType)
        {
            throw new BusinessException(PaperbaseErrorCodes.NoDocumentTypesConfigured);
        }

        var fileName = input.File.FileName ?? "document";
        var contentType = input.File.ContentType ?? "application/octet-stream";
        var extension = Path.GetExtension(fileName);

        await using var source = input.File.GetStream();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var fileSize = bytes.LongLength;

        var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var existing = await _documentRepository.FindByContentHashAsync(contentHash);
        if (existing != null)
        {
            var errorCode = existing.IsDeleted
                ? PaperbaseErrorCodes.DocumentInRecycleBin
                : PaperbaseErrorCodes.DocumentDuplicate;

            throw new BusinessException(errorCode)
                .WithData("FileName", fileName)
                .WithData("ExistingDocumentId", existing.Id);
        }

        var blobName = GuidGenerator.Create().ToString("N") + extension;
        using (var saveStream = new MemoryStream(bytes, writable: false))
        {
            await _blobContainer.SaveAsync(blobName, saveStream);
        }

        var sourceType = SourceType.Physical; // placeholder；提取完成后由 BackgroundJob 回写实际值
        var fileOrigin = new FileOrigin(
            CurrentUser.UserName ?? string.Empty,
            contentType,
            contentHash,
            fileSize,
            originalFileName: fileName);

        var document = new Document(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            blobName,
            sourceType,
            fileOrigin);

        await _documentRepository.InsertAsync(document, autoSave: true);

        await _distributedEventBus.PublishAsync(
            new DocumentUploadedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                FileName = fileName,
                FileSize = fileSize,
                ContentType = contentType
            });

        await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.TextExtraction);

        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    public virtual async Task<IRemoteStreamContent> GetBlobAsync(Guid id)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Default);

        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        var stream = await _blobContainer.GetAsync(document.OriginalFileBlobName);

        return new RemoteStreamContent(
            stream,
            document.FileOrigin.OriginalFileName,
            document.FileOrigin.ContentType,
            disposeStream: true);
    }

    [Authorize(PaperbasePermissions.Documents.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var document = await _documentRepository.GetAsync(id);

        await _documentRepository.DeleteAsync(id);

        // 通知下游消费方：Document 进入回收站，应将派生数据置为可恢复的归档状态
        await _distributedEventBus.PublishAsync(
            new DocumentDeletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = document.DocumentTypeCode
            });
    }

    [Authorize(PaperbasePermissions.Documents.PermanentDelete)]
    public virtual async Task PermanentDeleteAsync(Guid id)
    {
        Document document;
        using (DataFilter.Disable<ISoftDelete>())
        {
            document = await _documentRepository.GetAsync(id, includeDetails: true);
        }

        await _documentRepository.HardDeleteAsync(id);

        try
        {
            await _blobContainer.DeleteAsync(document.OriginalFileBlobName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Failed to delete blob {BlobName} for document {DocumentId}.",
                document.OriginalFileBlobName, id);
        }

        // 通知下游消费方：Document 已不可恢复，应物理删除派生数据
        await _distributedEventBus.PublishAsync(
            new DocumentPermanentlyDeletedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = document.DocumentTypeCode
            });
    }

    [Authorize(PaperbasePermissions.Documents.Restore)]
    public virtual async Task RestoreAsync(Guid id)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var document = await _documentRepository.GetAsync(id);
            if (!document.IsDeleted)
            {
                return;
            }

            document.IsDeleted = false;
            document.DeletionTime = null;
            document.DeleterId = null;

            await _documentRepository.UpdateAsync(document);

            await _distributedEventBus.PublishAsync(
                new DocumentRestoredEto
                {
                    DocumentId = document.Id,
                    TenantId = document.TenantId,
                    EventTime = Clock.Now,
                    DocumentTypeCode = document.DocumentTypeCode
                });
        }
    }

    [Authorize(PaperbasePermissions.Documents.Export)]
    public virtual async Task<IRemoteStreamContent> GetExportAsync(GetDocumentListInput input)
    {
        var query = await _documentRepository.GetQueryableAsync();
        query = ApplyFilter(query, input);
        query = ApplySorting(query, input.Sorting);

        var documents = await AsyncExecuter.ToListAsync(query);
        var csv = BuildDocumentCsv(documents);
        var bytes = Encoding.UTF8.GetBytes(csv);

        return new RemoteStreamContent(new MemoryStream(bytes), "documents.csv", "text/csv");
    }

    /// <summary>
    /// 重试单条 pipeline。当前仅 <see cref="PipelineRunStatus.Failed"/> 可重试；
    /// Pending/Running 抛 <c>PipelineRetryInProgress</c>，Succeeded/Skipped 抛 <c>PipelineNotRetryable</c>。
    /// 重试先创建 Pending Run，再把带 PipelineRunId 的 BackgroundJob 入队。
    /// 链式重放语义（隐式）：重试 <c>text-extraction</c> → 成功后链触发 <c>classification</c>。
    /// </summary>
    [Authorize(PaperbasePermissions.Documents.Pipelines.Retry)]
    public virtual async Task RetryPipelineAsync(Guid id, RetryPipelineInput input)
    {
        if (!PaperbasePipelines.RetryablePipelines.Contains(input.PipelineCode))
        {
            throw new BusinessException(PaperbaseErrorCodes.UnknownPipelineCode)
                .WithData("PipelineCode", input.PipelineCode);
        }

        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // 显式租户断言 + 软删除门：不依赖 ambient DataFilter。
        if (document.TenantId != CurrentTenant.Id)
        {
            Logger.LogWarning(
                "RetryPipelineAsync tenant mismatch: doc={DocumentId} docTenant={DocTenantId} currentTenant={CurrentTenantId}",
                document.Id, document.TenantId, CurrentTenant.Id);
            throw new EntityNotFoundException(typeof(Document), id);
        }

        if (document.IsDeleted)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentInRecycleBin)
                .WithData("FileName", document.OriginalFileBlobName);
        }

        var latestRun = document.GetLatestRun(input.PipelineCode);
        if (latestRun == null)
        {
            throw new BusinessException(PaperbaseErrorCodes.PipelineNeverRan)
                .WithData("PipelineCode", input.PipelineCode);
        }

        switch (latestRun.Status)
        {
            case PipelineRunStatus.Pending:
            case PipelineRunStatus.Running:
                throw new BusinessException(PaperbaseErrorCodes.PipelineRetryInProgress)
                    .WithData("PipelineCode", input.PipelineCode);
            case PipelineRunStatus.Succeeded:
            case PipelineRunStatus.Skipped:
                throw new BusinessException(PaperbaseErrorCodes.PipelineNotRetryable)
                    .WithData("PipelineCode", input.PipelineCode)
                    .WithData("Status", latestRun.Status.ToString());
        }

        Logger.LogInformation(
            "RetryPipelineAsync user={UserId} tenant={TenantId} doc={DocumentId} pipeline={PipelineCode} previousAttempt={Attempt}",
            CurrentUser.Id, CurrentTenant.Id, document.Id, input.PipelineCode, latestRun.AttemptNumber);

        await _pipelineJobScheduler.QueueAsync(document, input.PipelineCode);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ConfirmClassificationAsync(Guid id, ConfirmClassificationInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeCode);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ReclassifyAsync(Guid id, ReclassifyDocumentInput input)
    {
        return await ApplyManualClassificationAsync(id, input.DocumentTypeCode);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> ApproveReviewAsync(Guid id)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        if (document.ReviewStatus != DocumentReviewStatus.PendingReview)
        {
            // 幂等：不在 PendingReview 状态下返回当前快照，不抛
            return ObjectMapper.Map<Document, DocumentDto>(document);
        }

        document.ApproveReview();

        // 兑现 CLAUDE.md "OCR 置信度门槛" 承诺："操作员手动确认通过 → 触发 DocumentReadyEto"。
        // 两类待审核场景：
        //   (a) OCR confidence 不达标 → classification 尚未跑：schedule classification pipeline，
        //       让它正常推进，完成后由 DeriveLifecycle 跃到 Ready 自动发 DocumentReadyEto。
        //   (b) classification 已跑且有 DocumentTypeCode：直接 RecomputeLifecycle 让它进 Ready。
        //       注：分类置信度低 + DocumentTypeCode null 的场景应走 ReclassifyAsync 而非 ApproveAsync——
        //       走到这里时 RecomputeLifecycle 判定 Processing（type 空不满足 Ready），不发事件，符合预期。
        var hasClassificationRun = document.GetLatestRun(PaperbasePipelines.Classification) != null;
        if (!hasClassificationRun)
        {
            // QueueAsync 内已 _documentRepository.UpdateAsync(document, autoSave: true)，
            // 同一 document 实例的 ApproveReview() 状态变更随 scheduler 内的 save 一起落库；
            // 此分支无需再写一次。
            await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.Classification);
        }
        else
        {
            // RecomputeLifecycleAsync 仅修改 document 状态（in-memory），不写 DB——
            // 必须在这里显式 UpdateAsync 才能把 ApproveReview + RecomputeLifecycle 的变更落库。
            await _pipelineRunManager.RecomputeLifecycleAsync(document);
            await _documentRepository.UpdateAsync(document, autoSave: true);
        }

        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> RejectReviewAsync(Guid id, RejectReviewInput input)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);
        document.RejectReview(input.Reason);
        await _documentRepository.UpdateAsync(document, autoSave: true);
        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    /// <summary>
    /// Confirm 与 Reclassify 共享实现：写入 TypeCode + Reviewed 状态，
    /// 发布 DocumentClassifiedEto 让下游消费方重跑字段抽取。
    /// </summary>
    protected virtual async Task<DocumentDto> ApplyManualClassificationAsync(Guid id, string documentTypeCode)
    {
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // typeCode 校验责任在 AppService（不再走 manager 内部 EnsureRegisteredTypeCodeAsync）：
        // 按 Document.TenantId 精确单层匹配（CLAUDE.md "两层 mutually exclusive"）；
        // 不存在则 fail-fast，避免写入"业务模块订阅者认不出的 typeCode"。
        var typeDef = await _documentTypeRepository.FindByTypeCodeAsync(documentTypeCode);
        if (typeDef == null)
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidDocumentTypeCode)
                .WithData(nameof(documentTypeCode), documentTypeCode);
        }

        var run = await _pipelineRunManager.QueueAsync(document, PaperbasePipelines.Classification);
        await _pipelineRunManager.BeginAsync(document, run);

        await _pipelineRunManager.CompleteManualClassificationAsync(document, run, typeDef);
        await _distributedEventBus.PublishAsync(
            new DocumentClassifiedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = documentTypeCode,
                ClassificationConfidence = 1.0,
                Markdown = document.Markdown
            });

        await _documentRepository.UpdateAsync(document, autoSave: true);

        return ObjectMapper.Map<Document, DocumentDto>(document);
    }

    protected virtual IQueryable<Document> ApplyFilter(IQueryable<Document> query, GetDocumentListInput input)
    {
        if (input.LifecycleStatus.HasValue)
            query = query.Where(x => x.LifecycleStatus == input.LifecycleStatus.Value);

        if (!input.DocumentTypeCode.IsNullOrWhiteSpace())
            query = query.Where(x => x.DocumentTypeCode == input.DocumentTypeCode);

        if (input.ReviewStatus.HasValue)
            query = query.Where(d => d.ReviewStatus == input.ReviewStatus.Value);

        return query;
    }

    protected virtual IQueryable<Document> ApplySorting(IQueryable<Document> query, string? sorting)
    {
        return sorting switch
        {
            "creationTime" => query.OrderBy(x => x.CreationTime),
            _ => query.OrderByDescending(x => x.CreationTime)
        };
    }

    private static string BuildDocumentCsv(List<Document> documents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,DocumentTypeCode,LifecycleStatus,OriginalFileName,ContentType,CreationTime");

        foreach (var d in documents)
        {
            sb.AppendLine(string.Join(",",
                d.Id,
                EscapeCsv(d.DocumentTypeCode),
                d.LifecycleStatus.ToString(),
                EscapeCsv(d.FileOrigin.OriginalFileName),
                EscapeCsv(d.FileOrigin.ContentType),
                d.CreationTime.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (value.IsNullOrEmpty()) return string.Empty;
        if (value!.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
