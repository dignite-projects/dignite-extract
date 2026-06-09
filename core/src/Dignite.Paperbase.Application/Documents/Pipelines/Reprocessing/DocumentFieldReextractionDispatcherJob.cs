using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Pipelines.Reprocessing;

/// <summary>
/// 批量字段重抽的分发任务（#289 步骤 4）——链式自延续，系统里只剩一种短任务粒度。每次只处理一批：
/// keyset 分页只读 Id（<c>WHERE Id &gt; lastId ORDER BY Id Take(N)</c>，<c>AsNoTracking + Select(Id)</c>）→
/// enqueue 这批单篇 <see cref="DocumentFieldExtractionBackgroundJob"/> → 满批则 enqueue 下一个 dispatcher（带游标）→ 自己结束。
/// <para>
/// 范围固定按 <see cref="DocumentFieldReextractionDispatcherArgs.DocumentTypeId"/>（字段值离开类型无意义）；
/// 不排除人工确认（字段重抽不动分类、只重抽字段值，覆盖人工字段校正是已接受的轻代价）。
/// </para>
/// <para>
/// 诚实的代价（#289）：链式 at-least-once 重跑可能「分叉」（某 dispatcher commit 后、被标记成功前崩溃，重跑会再
/// enqueue 一批 + 下一个 dispatcher）。结果仍正确（单篇 <c>SetFields</c> 整组替换幂等），最坏多花成本，可接受。
/// </para>
/// </summary>
[BackgroundJobName("Paperbase.DocumentFieldReextractionDispatcher")]
public class DocumentFieldReextractionDispatcherJob
    : AsyncBackgroundJob<DocumentFieldReextractionDispatcherArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DocumentFieldReextractionDispatcherJob(
        IDocumentRepository documentRepository,
        IBackgroundJobManager backgroundJobManager,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager)
    {
        _documentRepository = documentRepository;
        _backgroundJobManager = backgroundJobManager;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
    }

    public override async Task ExecuteAsync(DocumentFieldReextractionDispatcherArgs args)
    {
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;

        // 显式恢复目标租户上下文——后台 worker 不一定自动还原；id 范围查询经 ambient IMultiTenant filter 隔离。
        using (_currentTenant.Change(args.TenantId))
        {
            List<Guid> ids;

            // 每批一个短 UoW：读 Id + enqueue 单篇任务 + enqueue 下一个 dispatcher 原子提交。
            using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                // args.DocumentTypeId 的层归属已由 DocumentReprocessingAppService.EnsureTypeInCurrentLayerAsync
                // 在入队前校验；这里只信任授权 AppService 产出的 args。即便传入跨层 type，ambient IMultiTenant
                // 过滤器也会让 GetIdsForReprocessingAsync 命中零行（fail-closed，不泄漏）。
                ids = await _documentRepository.GetIdsForReprocessingAsync(
                    documentTypeId: args.DocumentTypeId,
                    reviewStatus: null,
                    excludeManuallyConfirmed: false,
                    afterId: args.AfterId,
                    maxCount: batchSize);

                foreach (var id in ids)
                {
                    // 批量路径刻意不做 EnsureNotInProgress（与单篇 ReextractFieldsAsync 不对称）：并发的批量 + 单篇
                    // 重抽可能让同一文档跑两个 field-extraction run，但 FieldExtractionService 的 SetFields 整组替换幂等。
                    // 并发写同一文档时 Document 的乐观并发戳让落败者抛 AbpDbConcurrencyException → run 标记 Failed →
                    // ABP 重试干净重抽，终态一致。最坏只是多一次 LLM 成本——与本文件链式「分叉」同源的已接受代价。
                    await _backgroundJobManager.EnqueueAsync(
                        new DocumentFieldExtractionJobArgs { DocumentId = id });
                }

                // 满批 → 范围还有下一页，游标 = 本批末 Id，链式自延续；不满批 → 读尽，结束。
                if (ids.Count == batchSize)
                {
                    await _backgroundJobManager.EnqueueAsync(
                        new DocumentFieldReextractionDispatcherArgs
                        {
                            DocumentTypeId = args.DocumentTypeId,
                            TenantId = args.TenantId,
                            AfterId = ids[^1]
                        });
                }

                await uow.CompleteAsync();
            }

            Logger.LogInformation(
                "Field re-extraction dispatcher: enqueued {Count} document(s) for type {DocumentTypeId} (afterId={AfterId}, continued={Continued}).",
                ids.Count, args.DocumentTypeId, args.AfterId, ids.Count == batchSize);
        }
    }
}

public class DocumentFieldReextractionDispatcherArgs
{
    public Guid DocumentTypeId { get; set; }
    public Guid? TenantId { get; set; }

    /// <summary>keyset 游标：仅枚举 <c>Id &gt; AfterId</c> 的文档；首批为 null（从头）。</summary>
    public Guid? AfterId { get; set; }
}
