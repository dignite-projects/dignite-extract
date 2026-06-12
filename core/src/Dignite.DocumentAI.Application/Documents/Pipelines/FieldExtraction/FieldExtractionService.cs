using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Documents.Review;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.DocumentAI.Documents.Pipelines.FieldExtraction;

/// <summary>
/// 统一字段抽取执行引擎（#289 步骤 1）。把原先内联在 <see cref="FieldExtractionEventHandler"/> 里的
/// 「读字段定义 → <see cref="FieldExtractionWorkflow.ExtractAsync"/> → in-flight 守卫 → <c>Document.SetFields</c>
/// → 发 <see cref="FieldsExtractedEto"/>」核心抽取动作提炼为可复用单元，供两类触发方共用：
/// <list type="bullet">
///   <item>分类完成事件级联（<see cref="FieldExtractionEventHandler"/>，保留事件层 stale / 跨租户守卫后委托此引擎）；</item>
///   <item>批量 / 单篇「字段重抽」重处理（<c>field-extraction</c> pipeline 后台作业，#289 步骤 2-4）。</item>
/// </list>
/// <para>
/// 引擎按 <b>Document 当前 <see cref="Document.DocumentTypeId"/></b> 抽取（#207）——调用方只需给
/// <paramref name="documentId"/> + <paramref name="tenantId"/>，无需知道类型。<paramref name="expectedEventTypeCode"/>
/// 仅事件路径传入：用于 stale reclassify 事件的早退优化（事件携带的旧 TypeCode 解析到与当前 Document 不同的类型
/// → 跳过，等新事件触发新一轮）。批量路径传 <c>null</c>，永远按当前类型抽取。
/// </para>
/// <para>
/// 安全约束（CLAUDE.md "## 安全约定"）：显式 <see cref="ICurrentTenant.Change"/> 恢复目标 TenantId 上下文，
/// 让 ABP <c>IMultiTenant</c> filter 自动按层隔离仓储查询；跨租户断言（防 ambient filter 被 disable）；
/// in-flight reclassify race 断言（LLM 飞行期间类型被改 → 丢弃，防旧 schema 污染 ExtractedFields）。
/// </para>
/// <para>
/// UoW 三段式（<c>.claude/rules/background-jobs.md</c>）：读 FieldDefinition / 回查 Document.Markdown /
/// LLM 调用 / 写 Document + publish 各阶段 <c>requiresNew</c> 短 UoW——LLM 外部调用永不被任何长事务包住。
/// 调用方（事件 handler / 后台作业）须在 ambient UoW 关闭（<c>[UnitOfWork(IsDisabled = true)]</c>）或独立短 UoW
/// 上下文中调用本方法。
/// </para>
/// </summary>
public class FieldExtractionService : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ReviewStateEvaluator _reviewEvaluator;
    private readonly FieldExtractionWorkflow _workflow;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly IClock _clock;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    // 后台作业路径上 ABP 的 BackgroundJobExecuter 会用 ICancellationTokenProvider.Use(...) 压入
    // 作业执行上下文的取消令牌（与 DocumentTextExtractionBackgroundJob 同例）；事件路径无 ambient
    // 令牌时回退 CancellationToken.None，行为不变。
    private readonly ICancellationTokenProvider _cancellationTokenProvider;
    private readonly ILogger<FieldExtractionService> _logger;

    public FieldExtractionService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        ReviewStateEvaluator reviewEvaluator,
        FieldExtractionWorkflow workflow,
        IDistributedEventBus distributedEventBus,
        IClock clock,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        ICancellationTokenProvider cancellationTokenProvider,
        ILogger<FieldExtractionService> logger)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _reviewEvaluator = reviewEvaluator;
        _workflow = workflow;
        _distributedEventBus = distributedEventBus;
        _clock = clock;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _cancellationTokenProvider = cancellationTokenProvider;
        _logger = logger;
    }

    /// <summary>
    /// 对单个文档按其当前类型执行一次完整字段抽取（整组替换 + 发 <see cref="FieldsExtractedEto"/>）。幂等：
    /// 重复调用对同一终态产出一致结果，重投无害（#289「幂等是基石」）。任一前置守卫不满足（文档缺失 /
    /// 跨租户 / 未分类 / stale 事件 / 飞行期间被 reclassify）→ 返回 <see cref="FieldExtractionOutcome.Skipped"/>，不写不发。
    /// </summary>
    /// <param name="documentId">目标文档 Id。</param>
    /// <param name="tenantId">目标文档所属租户（决定字段定义层）；引擎据此 <see cref="ICurrentTenant.Change"/>。</param>
    /// <param name="expectedEventTypeCode">事件路径传入的旧 TypeCode（stale 事件早退优化）；批量路径传 <c>null</c>。</param>
    public virtual async Task<FieldExtractionResult> ExtractAsync(
        Guid documentId,
        Guid? tenantId,
        string? expectedEventTypeCode = null)
    {
        // 显式恢复目标租户上下文 —— 后台 / 事件 handler 在 Hangfire / worker 上下文中
        // ICurrentTenant 不一定自动还原。
        using (_currentTenant.Change(tenantId))
        {
            // 阶段 1：短 UoW —— 以 Document 当前内部 DocumentTypeId 为准读类型 / 字段定义（#207）。
            // 显式 dispose 让该 UoW 完全退出，再进入阶段 2 的外部 LLM 调用。
            Guid documentTypeId;
            string documentTypeCode;
            List<FieldDefinition> definitions;
            string markdown;
            using (var readUow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                var readDocument = await _documentRepository.FindAsync(documentId, includeDetails: false);
                if (readDocument == null)
                {
                    _logger.LogWarning(
                        "Field extraction requested for missing document {DocumentId} — skipped.",
                        documentId);
                    return FieldExtractionResult.Skipped;
                }

                // 跨租户断言（防 ambient DataFilter 被 disable 的路径）。
                if (readDocument.TenantId != tenantId)
                {
                    _logger.LogWarning(
                        "Cross-tenant field extraction discarded: requested tenant={RequestedTenant} document tenant={DocTenant} document={DocId}",
                        tenantId, readDocument.TenantId, documentId);
                    return FieldExtractionResult.Skipped;
                }

                if (!readDocument.DocumentTypeId.HasValue)
                {
                    _logger.LogInformation(
                        "Field extraction requested for unclassified document {DocumentId}; skipped.",
                        documentId);
                    return FieldExtractionResult.Skipped;
                }

                documentTypeId = readDocument.DocumentTypeId.Value;

                var currentType = await _documentTypeRepository.FindAsync(documentTypeId, includeDetails: false);
                if (currentType == null)
                {
                    _logger.LogWarning(
                        "Document {DocumentId} references missing DocumentTypeId {DocumentTypeId}; field extraction skipped.",
                        documentId, documentTypeId);
                    return FieldExtractionResult.Skipped;
                }

                documentTypeCode = currentType.TypeCode;

                // 事件路径专属：stale reclassify 事件早退优化。事件携带的旧 TypeCode 若解析到与当前
                // Document 不同的类型，说明本事件已 stale（飞行期间被 reclassify），跳过等新事件。
                // 批量路径传 expectedEventTypeCode=null，恒按当前类型抽取，不做此早退。
                if (expectedEventTypeCode != null)
                {
                    var eventType = await _documentTypeRepository.FindByTypeCodeAsync(expectedEventTypeCode);
                    if (eventType != null && eventType.Id != documentTypeId)
                    {
                        _logger.LogInformation(
                            "Stale classification event before field extraction: event typeCode={EventTypeCode} (typeId={EventTypeId}) " +
                            "document typeId={DocTypeId} doc={DocumentId}.",
                            expectedEventTypeCode, eventType.Id, documentTypeId, documentId);
                        return FieldExtractionResult.Skipped;
                    }

                    if (eventType == null && !string.Equals(expectedEventTypeCode, documentTypeCode, StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "Classification event typeCode={EventTypeCode} is no longer resolvable in tenant {TenantId}; " +
                            "continuing field extraction for doc {DocumentId} with current typeCode={CurrentTypeCode} and stable typeId={DocumentTypeId}.",
                            expectedEventTypeCode, tenantId, documentId, documentTypeCode, documentTypeId);
                    }
                }

                definitions = await _fieldDefinitionRepository.GetListAsync(documentTypeId);
                markdown = readDocument.Markdown ?? string.Empty;
                await readUow.CompleteAsync();
            }

            // 空字段路径：目标类型无字段定义。仍需把该文档可能残留的旧 schema 字段行清空——
            // reclassify 从「有字段类型」换到「无字段类型」时，旧字段行不清会以新 TypeCode 被结构化检索 /
            // DTO 误带（违反「reclassify 整组替换、不残留旧 schema」语义）。短 UoW 内清空 + publish。
            if (definitions.Count == 0)
            {
                using var clearUow = _unitOfWorkManager.Begin(requiresNew: true);

                var blankDocument = await _documentRepository.FindWithFieldValuesAsync(documentId);
                if (blankDocument == null)
                {
                    _logger.LogWarning(
                        "Field extraction requested for missing document {DocumentId} — skipped.",
                        documentId);
                    return FieldExtractionResult.Skipped;
                }

                if (blankDocument.TenantId != tenantId)
                {
                    _logger.LogWarning(
                        "Cross-tenant field extraction discarded: requested tenant={RequestedTenant} document tenant={DocTenant} document={DocId}",
                        tenantId, blankDocument.TenantId, documentId);
                    return FieldExtractionResult.Skipped;
                }

                // 仅当当前类型仍是阶段 1 捕获的类型（非 reclassify race 的 stale 事件）才清空，
                // 避免用 stale 事件误删后续分类写入的字段。按内部 DocumentTypeId 比较（#207）。
                if (blankDocument.DocumentTypeId != documentTypeId)
                {
                    _logger.LogInformation(
                        "Stale field extraction while clearing empty fields: document typeId={DocTypeId} expected typeId={ExpectedTypeId} doc={DocumentId}.",
                        blankDocument.DocumentTypeId, documentTypeId, documentId);
                    return FieldExtractionResult.Skipped;
                }

                // #284：无字段定义 ⟹ 无必填可言，清 MissingRequiredFields（reclassify 到无字段类型时退出必填队列）。
                var hadFields = blankDocument.ExtractedFieldValues.Count > 0;
                var hadMissingRequired =
                    (blankDocument.ReviewReasons & DocumentReviewReasons.MissingRequiredFields) != DocumentReviewReasons.None;
                if (hadFields || hadMissingRequired)
                {
                    blankDocument.SetFields(Array.Empty<DocumentFieldValue>());
                    blankDocument.SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
                    await _documentRepository.UpdateAsync(blankDocument, autoSave: true);
                }

                await PublishFieldsExtractedAsync(documentId, tenantId, fieldCount: 0, documentTypeCode);
                await clearUow.CompleteAsync();
                return FieldExtractionResult.Cleared;
            }

            var descriptors = definitions.Select(d => new FieldExtractionDescriptor(
                d.Id, d.Name, d.Prompt, d.DataType, d.IsRequired, d.AllowMultiple)).ToList();

            // 阶段 2：外部 LLM 调用，**不在任何 UoW 内**（background-jobs.md 硬约束）。
            // 到这里阶段 1 的短 UoW 已 dispose，_unitOfWorkManager.Current 应为 null。
            if (_unitOfWorkManager.Current != null)
            {
                _logger.LogWarning(
                    "FieldExtractionService entered external LLM call with ambient UoW present (doc={DocumentId}). " +
                    "This violates background-jobs.md (external work must not run inside a long-lived UoW). " +
                    "Check the caller's UoW boundaries and readUow dispose ordering.",
                    documentId);
            }

            var extracted = await _workflow.ExtractAsync(descriptors, markdown, _cancellationTokenProvider.Token);

            // 阶段 3：短 UoW 写 Document + publish FieldsExtractedEto——两件事在同一 UoW 内由 ABP outbox
            // 原子持久化，避免"字段写入成功但事件丢失"。
            using var writeUow = _unitOfWorkManager.Begin(requiresNew: true);

            var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
            if (document == null)
            {
                _logger.LogWarning(
                    "Field extraction requested for missing document {DocumentId} — skipped.",
                    documentId);
                return FieldExtractionResult.Skipped;
            }

            if (document.TenantId != tenantId)
            {
                _logger.LogWarning(
                    "Cross-tenant field extraction discarded: requested tenant={RequestedTenant} document tenant={DocTenant} document={DocId}",
                    tenantId, document.TenantId, documentId);
                return FieldExtractionResult.Skipped;
            }

            // In-flight reclassify race 断言：若 Document 当前的 DocumentTypeId 已与阶段 1 捕获的类型 Id 不一致，
            // 说明 LLM 飞行期间被 reclassify。继续抽取会用旧 schema 污染 ExtractedFields——丢弃本次。
            if (document.DocumentTypeId != documentTypeId)
            {
                _logger.LogInformation(
                    "Reclassified during field extraction: captured typeId={CapturedTypeId} current typeId={DocTypeId} doc={DocumentId}. " +
                    "Discarding to avoid writing fields against an outdated schema.",
                    documentTypeId, document.DocumentTypeId, documentId);
                return FieldExtractionResult.Skipped;
            }

            // LLM 调用期间字段定义可能被 admin 改名 / 改类型 / 删除。写入前按稳定 Id 重读一次。
            var currentDefinitions = await _fieldDefinitionRepository.GetListAsync(documentTypeId);
            var currentDefinitionsById = currentDefinitions.ToDictionary(d => d.Id);

            var fieldValues = new List<DocumentFieldValue>();
            foreach (var d in descriptors)
            {
                if (!extracted.TryGetValue(d.Name, out var value) || !value.HasValue)
                {
                    continue;
                }

                if (!currentDefinitionsById.TryGetValue(d.FieldDefinitionId, out var currentDefinition))
                {
                    _logger.LogInformation(
                        "FieldDefinition {FieldDefinitionId} was removed or disabled during extraction for doc {DocumentId}; extracted value skipped.",
                        d.FieldDefinitionId, documentId);
                    continue;
                }

                if (currentDefinition.DataType != d.DataType)
                {
                    _logger.LogWarning(
                        "FieldDefinition {FieldDefinitionId} DataType changed during extraction for doc {DocumentId}: {OldDataType} -> {NewDataType}; stale value skipped.",
                        d.FieldDefinitionId, documentId, d.DataType, currentDefinition.DataType);
                    continue;
                }

                if (currentDefinition.AllowMultiple != d.AllowMultiple)
                {
                    _logger.LogWarning(
                        "FieldDefinition {FieldDefinitionId} AllowMultiple changed during extraction for doc {DocumentId}: {OldAllowMultiple} -> {NewAllowMultiple}; stale value skipped.",
                        d.FieldDefinitionId, documentId, d.AllowMultiple, currentDefinition.AllowMultiple);
                    continue;
                }

                if (!ExtractedFieldValueValidator.IsValid(value.Value, currentDefinition.DataType, currentDefinition.AllowMultiple))
                {
                    _logger.LogWarning(
                        "FieldExtractionWorkflow returned an invalid {DataType} (multi={AllowMultiple}) value for field {FieldName} ({FieldDefinitionId}) on doc {DocumentId}; value skipped.",
                        currentDefinition.DataType, currentDefinition.AllowMultiple, currentDefinition.Name, currentDefinition.Id, documentId);
                    continue;
                }

                fieldValues.AddRange(DocumentFieldValueFactory.Expand(
                    currentDefinition.Id, currentDefinition.DataType, currentDefinition.AllowMultiple, value.Value));
            }

            document.SetFields(fieldValues);

            // #284：抽取完成那一刻评估必填缺失并物化 MissingRequiredFields（读路径无法判断「是否已抽取」，故必须写时定）。
            // 复用写入前重读的 currentDefinitions（筛 IsRequired）+ 写入后的 ExtractedFieldValues；本处在 stale 守卫之后，
            // stale 事件到不了此处，零新增 race。non-blocking——置 MRF 不会把已 Ready 文档打回（DeriveLifecycle 只看 blocking）。
            var requiredIds = currentDefinitions.Where(cd => cd.IsRequired).Select(cd => cd.Id).ToList();
            var extractedIds = document.ExtractedFieldValues.Select(v => v.FieldDefinitionId).Distinct().ToList();
            document.SetReviewReason(
                DocumentReviewReasons.MissingRequiredFields,
                _reviewEvaluator.MissingRequiredFieldsPresent(requiredIds, extractedIds));

            // FieldsExtractedEto.FieldCount 是逻辑字段数（拿到值的不同字段个数），非展开后的行数。
            var fieldCount = fieldValues.Select(v => v.FieldDefinitionId).Distinct().Count();

            await _documentRepository.UpdateAsync(document, autoSave: true);
            await PublishFieldsExtractedAsync(documentId, tenantId, fieldCount, documentTypeCode);

            await writeUow.CompleteAsync();

            _logger.LogInformation(
                "Field extraction for document {DocumentId} produced {NonNullCount}/{TotalCount} non-null fields ({RowCount} value rows).",
                documentId, fieldCount, definitions.Count, fieldValues.Count);

            return FieldExtractionResult.Extracted(fieldCount);
        }
    }

    private async Task PublishFieldsExtractedAsync(Guid documentId, Guid? tenantId, int fieldCount, string documentTypeCode)
    {
        await _distributedEventBus.PublishAsync(
            new FieldsExtractedEto
            {
                DocumentId = documentId,
                TenantId = tenantId,
                EventTime = _clock.Now,
                DocumentTypeCode = documentTypeCode,
                FieldCount = fieldCount
            });
    }
}
