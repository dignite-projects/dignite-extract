using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L2 自动触发：执行 <see cref="RelationDiscoveryService.DiscoverAsync"/>，
/// 用 <see cref="DocumentPipelineRun"/> 跟踪运行状态以便观察 / 后续诊断。
///
/// <para>
/// 短 UoW 模式（参见 <c>.claude/rules/background-jobs.md</c>）：
/// <list type="number">
/// <item>Begin：UoW1 加载 Document，标记 PipelineRun 为 Running，提交。</item>
/// <item>Discovery：UoW2 调用 <see cref="RelationDiscoveryService"/>，创建 AiSuggested 关系，提交。</item>
/// <item>Complete / Fail：UoW3 重新加载 Document，标记 PipelineRun 状态，提交。</item>
/// </list>
/// L2 本身只做 DB 查询（无 LLM / 文件 IO / 长 CPU），但仍然分阶段——
/// 保持与其他 pipeline 一致的运行模型，便于将来加 telemetry / 重试。
/// </para>
///
/// <para>
/// 失败处理：DiscoverAsync 内部已对 provider 异常做隔离（见 #118 commit ffe745a）；
/// 此层 try/catch 兜底捕获基础设施异常（DB 连接断开 / 序列化错误），
/// 不会因为某个有 bug 的 provider 把整个 PipelineRun 拖成 Failed。
/// </para>
/// </summary>
[BackgroundJobName("Paperbase.RelationDiscovery")]
public class RelationDiscoveryBackgroundJob
    : AsyncBackgroundJob<RelationDiscoveryJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly DocumentPipelineRunAccessor _pipelineRunAccessor;
    private readonly RelationDiscoveryService _discoveryService;
    private readonly SemanticRelationDiscoveryService _semanticDiscoveryService;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public RelationDiscoveryBackgroundJob(
        IDocumentRepository documentRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        RelationDiscoveryService discoveryService,
        SemanticRelationDiscoveryService semanticDiscoveryService,
        IUnitOfWorkManager unitOfWorkManager)
    {
        _documentRepository = documentRepository;
        _pipelineRunManager = pipelineRunManager;
        _pipelineRunAccessor = pipelineRunAccessor;
        _discoveryService = discoveryService;
        _semanticDiscoveryService = semanticDiscoveryService;
        _unitOfWorkManager = unitOfWorkManager;
    }

    public override async Task ExecuteAsync(RelationDiscoveryJobArgs args)
    {
        var workItem = await BeginRunAsync(args);
        if (workItem == null)
        {
            return;
        }

        int createdCount;
        try
        {
            createdCount = await DiscoverAsync(workItem.DocumentId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "L2 RelationDiscovery failed for document {DocumentId}. PipelineRun marked failed; document lifecycle unchanged (non-key pipeline).",
                workItem.DocumentId);
            await FailRunAsync(workItem.DocumentId, workItem.RunId, ex.Message);
            return;
        }

        await CompleteRunAsync(workItem.DocumentId, workItem.RunId, createdCount);
    }

    protected virtual async Task<DiscoveryWorkItem?> BeginRunAsync(RelationDiscoveryJobArgs args)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.FindAsync(args.DocumentId, includeDetails: true);
        if (document == null)
        {
            // Document was hard-deleted between event publish and job pickup — silently drop.
            // No PipelineRun to mark; Document carrying the run is gone.
            Logger.LogInformation(
                "L2 RelationDiscovery: document {DocumentId} no longer exists; dropping job.",
                args.DocumentId);
            await uow.CompleteAsync();
            return null;
        }

        var run = await _pipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, PaperbasePipelines.RelationDiscovery);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        return new DiscoveryWorkItem(run.Id, document.Id);
    }

    protected virtual async Task<int> DiscoverAsync(Guid documentId)
    {
        // L2: structured fan-out across business-module providers. Cheap (DB queries only).
        // L2 writes commit in a dedicated UoW (autoSave: false on inserts ⇒ uow.CompleteAsync()).
        int l2Count;
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var created = await _discoveryService.DiscoverAsync(documentId);
            await uow.CompleteAsync();
            l2Count = created.Count;
        }

        if (l2Count > 0)
        {
            // L2 found structured matches — that's strong signal; don't run L3 (expensive LLM)
            // on top. L3 is the fallback for "structured matching found nothing".
            return l2Count;
        }

        // L3 fallback: vector recall + LLM evaluation. Disabled by default (operator opt-in).
        // NOT wrapped in an outer UoW: SemanticRelationDiscoveryService uses autoSave: true on
        // each relation insert (per-call implicit UoW), so LLM calls between candidates run with
        // no ambient UoW — satisfies the "no DB connection during external work" rule.
        var l3Created = await _semanticDiscoveryService.DiscoverAsync(documentId);
        return l3Created.Count;
    }

    protected virtual async Task CompleteRunAsync(Guid documentId, Guid runId, int createdCount)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
        var run = document.GetRun(runId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, runId, PaperbasePipelines.RelationDiscovery);

        await _pipelineRunManager.CompleteAsync(document, run);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        Logger.LogInformation(
            "L2 RelationDiscovery: document {DocumentId} run {RunId} succeeded; created {CreatedCount} AiSuggested relations.",
            documentId, runId, createdCount);
    }

    protected virtual async Task FailRunAsync(Guid documentId, Guid runId, string errorMessage)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
        var run = document.GetRun(runId)
            ?? await _pipelineRunAccessor.BeginOrStartAsync(
                document, runId, PaperbasePipelines.RelationDiscovery);

        await _pipelineRunManager.FailAsync(document, run, errorMessage);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    protected sealed record DiscoveryWorkItem(Guid RunId, Guid DocumentId);
}

public class RelationDiscoveryJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? PipelineRunId { get; set; }
}
