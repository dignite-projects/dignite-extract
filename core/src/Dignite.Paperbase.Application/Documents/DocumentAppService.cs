using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IDistributedEventBus _distributedEventBus;

    public DocumentAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        ICabinetRepository cabinetRepository,
        IBlobContainer<PaperbaseDocumentContainer> blobContainer,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IDistributedEventBus distributedEventBus)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _cabinetRepository = cabinetRepository;
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

        // ExtractedFields 字段值过滤器：把每个 FieldFilter 解析成带声明类型的 DocumentFieldQuery
        // （FieldDefinition 跨聚合查询，属调用层职责）。任一字段未在该类型下定义 → loud fail
        // （UnknownExtractedField，可纠正信号），不静默空。无 FieldFilters → null（仅元数据检索）。
        // 输入结构校验（DocumentTypeCode 必填 / 数量 / 长度 / 每个 filter 至少一个值）已由 DTO 自动校验
        // （AbpValidationException）兜在方法体之前。
        var fieldQueries = await ResolveFieldQueriesAsync(input);

        // 回收站视图：需要 Restore 权限，且整个查询管道必须在 DataFilter.Disable<ISoftDelete> 作用域内
        if (input.IsDeleted == true)
        {
            await CheckPolicyAsync(PaperbasePermissions.Documents.Restore);
            using (DataFilter.Disable<ISoftDelete>())
            {
                return await ExecuteListQueryAsync(input, onlyDeleted: true, fieldQueries);
            }
        }

        return await ExecuteListQueryAsync(input, onlyDeleted: false, fieldQueries);
    }

    protected virtual async Task<List<DocumentFieldQuery>?> ResolveFieldQueriesAsync(GetDocumentListInput input)
    {
        if (input.FieldFilters is not { Count: > 0 })
        {
            return null;
        }

        // DTO 校验已保证有 FieldFilters 时 DocumentTypeCode 非空、每个 filter 有 Name + 至少一个值。
        var fieldQueries = new List<DocumentFieldQuery>(input.FieldFilters.Count);
        foreach (var filter in input.FieldFilters)
        {
            var definition = await _fieldDefinitionRepository.FindByNameAsync(input.DocumentTypeCode!, filter.Name!);
            if (definition == null)
            {
                throw new BusinessException(PaperbaseErrorCodes.UnknownExtractedField)
                    .WithData("FieldName", filter.Name!)
                    .WithData("DocumentTypeCode", input.DocumentTypeCode!);
            }

            fieldQueries.Add(new DocumentFieldQuery(
                filter.Name!, definition.DataType, filter.Value, filter.Min, filter.Max));
        }

        return fieldQueries;
    }

    protected virtual async Task<PagedResultDto<DocumentListItemDto>> ExecuteListQueryAsync(
        GetDocumentListInput input,
        bool onlyDeleted,
        List<DocumentFieldQuery>? fieldQueries)
    {
        // 用 WithDetailsAsync(选择器) 只 eager-load ExtractedFieldValues child 行（不含 PipelineRuns）——
        // 持久化无关（ABP 仓储 API，避免 App 层直接依赖 EF Core .Include）。一次性 JOIN 取回 → mapper 组装
        // ExtractedFields 字典，杜绝逐文档 N+1 / lazy loading（Issue #206 复核护栏）。
        var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);

        // ExtractedFields 字段值过滤：仓储用 Documents-anchored LINQ（child EXISTS + 类型化列比较）取（锚定
        // DocumentTypeCode 的）匹配 Id 集合，再与本查询求交——保持 ApplyFilter 为元数据过滤单一来源。
        if (fieldQueries is { Count: > 0 })
        {
            var matchedIds = await _documentRepository.GetFieldMatchedIdsAsync(input.DocumentTypeCode!, fieldQueries);
            query = query.Where(d => matchedIds.Contains(d.Id));
        }

        query = ApplyFilter(query, input);
        if (onlyDeleted)
        {
            query = query.Where(d => d.IsDeleted);
        }

        var totalCount = await AsyncExecuter.CountAsync(query);

        query = ApplySorting(query, input.Sorting);
        query = query.Skip(input.SkipCount).Take(input.MaxResultCount);

        var documents = await AsyncExecuter.ToListAsync(query);

        // ExtractedFields 由 mapper 从 ExtractedFieldValues 即时组装（无条件全带；消费方按 DocumentTypeCode 决定如何呈现）。
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

        // 文件柜归属校验（#194）：若指定 cabinetId，先断言 Cabinets 权限（fail-closed，与前端 canViewCabinets
        // gate 对称）——[Authorize(Documents.Upload)] 不覆盖 cabinet 归属，无此断言则无 Cabinets 权限者可绕过 UI
        // 把文档归到隐藏柜。再校验柜存在（租户隔离由 ambient IMultiTenant 过滤器施加，跨租户 FindAsync 返回 null）。
        // 柜正交于 pipeline——此处仅做上传时人工归属校验，后续 pipeline 不碰。
        if (input.CabinetId.HasValue)
        {
            await CheckPolicyAsync(PaperbasePermissions.Cabinets.Default);

            var cabinet = await _cabinetRepository.FindAsync(input.CabinetId.Value);
            if (cabinet == null)
            {
                throw new BusinessException(PaperbaseErrorCodes.InvalidCabinetId)
                    .WithData("CabinetId", input.CabinetId.Value);
            }
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
            fileOrigin,
            cabinetId: input.CabinetId);

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

        // 租户隔离由 ambient IMultiTenant 过滤器施加——GetAsync 对跨租户 id 已抛 EntityNotFound。
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

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

    /// <summary>
    /// 操作员手改字段抽取结果（个别纠错）。整体替换 ExtractedFields；key 必须是该文档所属层、
    /// 该 DocumentType 下已定义的字段名；完成后复用 FieldsExtractedEto 重发让下游同步。
    /// </summary>
    [Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
    public virtual async Task<DocumentDto> UpdateExtractedFieldsAsync(Guid id, UpdateExtractedFieldsInput input)
    {
        // 租户隔离由 ambient IMultiTenant 过滤器施加——GetAsync 对跨租户 id 已抛 EntityNotFound。
        var document = await _documentRepository.GetAsync(id, includeDetails: true);

        // 字段定义挂在 DocumentType 下——未分类无从校验字段名。
        if (string.IsNullOrWhiteSpace(document.DocumentTypeCode))
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentNotClassified);
        }

        // 校验每个 key 是该文档所属层、该 DocumentType 下已定义的字段名。
        // GetForExtractionAsync 按 ambient CurrentTenant.Id 查单层（已断言 == document.TenantId）。
        var definitions = await _fieldDefinitionRepository.GetForExtractionAsync(document.DocumentTypeCode);
        var definitionsByName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var fields = input.Fields ?? new Dictionary<string, JsonElement>();

        // 校验每个值符合声明类型后，构造 typed DocumentFieldValue（DataType 来自 FieldDefinition）。
        // 校验通过即可直接交给聚合根，不再经 JSON 字典中转——值类型与列对齐的转换集中在 DocumentExtractedField 内。
        var fieldValues = new List<DocumentFieldValue>(fields.Count);
        foreach (var (key, value) in fields)
        {
            if (!definitionsByName.TryGetValue(key, out var definition))
            {
                throw new BusinessException(PaperbaseErrorCodes.UnknownExtractedField)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", document.DocumentTypeCode);
            }

            if (!ExtractedFieldValueValidator.IsValid(value, definition.DataType))
            {
                throw new BusinessException(PaperbaseErrorCodes.InvalidExtractedFieldValue)
                    .WithData("FieldName", key)
                    .WithData("DocumentTypeCode", document.DocumentTypeCode)
                    .WithData("DataType", definition.DataType.ToString())
                    .WithData("JsonValueKind", value.ValueKind.ToString());
            }

            fieldValues.Add(new DocumentFieldValue(key, definition.DataType, value));
        }

        // 整组替换（与 FieldExtractionEventHandler 一致：空则清空全部字段行）。
        document.SetFields(fieldValues);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        // 复用 FieldsExtractedEto 重发——手改与 LLM 抽取对下游是同一种"字段已更新"信号，
        // 下游按 (DocumentId, EventType, EventTime) 幂等、回拉最新字段值（出口契约：薄载荷）。
        await _distributedEventBus.PublishAsync(
            new FieldsExtractedEto
            {
                DocumentId = document.Id,
                TenantId = document.TenantId,
                EventTime = Clock.Now,
                DocumentTypeCode = document.DocumentTypeCode,
                FieldCount = fieldValues.Count
            });

        return ObjectMapper.Map<Document, DocumentDto>(document);
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
                ClassificationConfidence = 1.0
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

        if (input.CabinetId.HasValue)
            query = query.Where(x => x.CabinetId == input.CabinetId.Value);

        if (input.ReviewStatus.HasValue)
        {
            query = query.Where(d => d.ReviewStatus == input.ReviewStatus.Value);

            // PendingReview 队列默认只展示仍需处理的文档。RejectReview 会保留
            // ReviewStatus=PendingReview 作为审计信号，但 lifecycle 已是 Failed；
            // 若调用方确实要查失败审核记录，可显式传 LifecycleStatus=Failed。
            if (input.ReviewStatus.Value == DocumentReviewStatus.PendingReview &&
                !input.LifecycleStatus.HasValue)
            {
                query = query.Where(d => d.LifecycleStatus != DocumentLifecycleStatus.Failed);
            }
        }

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
}
