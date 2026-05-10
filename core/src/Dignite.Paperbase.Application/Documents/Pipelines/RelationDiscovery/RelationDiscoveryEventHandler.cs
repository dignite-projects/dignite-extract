using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L2 自动触发：订阅 <see cref="DocumentClassifiedEto"/>，
/// 在文档分类完成后排队 L2 背景任务。
///
/// <para>
/// <strong>触发时序</strong>：核心层与业务模块都订阅 <see cref="DocumentClassifiedEto"/>。
/// ABP 分布式事件总线本地实现按注册顺序串行调用所有订阅者；本订阅者只做"排队后台任务"
/// 这种轻量动作（不直接执行 L2），所以不会阻塞业务模块的字段抽取。
/// 后台任务实际执行时，业务模块（如 contracts）的 <c>autoSave: true</c> 抽取已经完成，
/// L2 通过 <c>IDocumentIdentifierProvider</c> 反查能拿到刚抽取的字段。
/// </para>
///
/// <para>
/// <strong>orphan 文档</strong>：分类失败的文档不会发布 <see cref="DocumentClassifiedEto"/>，
/// 自然跳过 L2。这与 #115 设计 "v1 orphan 走 L3 兜底" 一致。
/// </para>
///
/// <para>
/// <strong>幂等性</strong>：<see cref="DocumentPipelineJobScheduler.QueueAsync"/> 每次创建新的
/// <c>DocumentPipelineRun</c>。同一文档分类事件理论上只触发一次（除非分类被人工重跑），
/// 重复运行 L2 也安全：<see cref="RelationDiscoveryService"/> 对已存在关系做完全跳过，
/// 不会创建重复 AiSuggested 行。
/// </para>
/// </summary>
public class RelationDiscoveryEventHandler
    : IDistributedEventHandler<DocumentClassifiedEto>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly ILogger<RelationDiscoveryEventHandler> _logger;

    public RelationDiscoveryEventHandler(
        IDocumentRepository documentRepository,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        ILogger<RelationDiscoveryEventHandler> logger)
    {
        _documentRepository = documentRepository;
        _pipelineJobScheduler = pipelineJobScheduler;
        _logger = logger;
    }

    public virtual async Task HandleEventAsync(DocumentClassifiedEto eventData)
    {
        if (eventData.DocumentId == Guid.Empty)
        {
            return;
        }

        // Document might have been hard-deleted between classification publish and handler dispatch.
        // FindAsync (not GetAsync) → silently drop instead of throwing into the event bus.
        var document = await _documentRepository.FindAsync(eventData.DocumentId, includeDetails: true);
        if (document == null)
        {
            _logger.LogInformation(
                "L2 RelationDiscovery handler: document {DocumentId} no longer exists; skipping enqueue.",
                eventData.DocumentId);
            return;
        }

        await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.RelationDiscovery);
    }
}
