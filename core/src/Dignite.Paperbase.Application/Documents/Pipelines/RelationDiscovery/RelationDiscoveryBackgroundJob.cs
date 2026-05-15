using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115: background job that runs <see cref="RelationDiscoveryService.DiscoverAsync"/>
/// for a document and tracks the run on <see cref="DocumentPipelineRun"/>.
///
/// <para>
/// 短 UoW 模式（参见 <c>.claude/rules/background-jobs.md</c>）：
/// <list type="number">
/// <item>Begin：UoW1 加载 Document，标记 PipelineRun 为 Running，提交。</item>
/// <item>Discovery：UoW2 调用 <see cref="RelationDiscoveryService"/>，创建 AiSuggested 关系，提交。</item>
/// <item>Complete / Fail：UoW3 重新加载 Document，标记 PipelineRun 状态，提交。</item>
/// </list>
/// </para>
///
/// <para>
/// 失败处理：DiscoverAsync 内部已对 provider 异常做隔离；此层 try/catch 兜底捕获基础设施
/// 异常（DB 连接断开 / 序列化错误），不会因为某个有 bug 的 provider 把整个 PipelineRun 拖成 Failed。
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
    private readonly RelationDiscoveryTelemetryRecorder _telemetry;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public RelationDiscoveryBackgroundJob(
        IDocumentRepository documentRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        RelationDiscoveryService discoveryService,
        RelationDiscoveryTelemetryRecorder telemetry,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager)
    {
        _documentRepository = documentRepository;
        _pipelineRunManager = pipelineRunManager;
        _pipelineRunAccessor = pipelineRunAccessor;
        _discoveryService = discoveryService;
        _telemetry = telemetry;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
    }

    public override async Task ExecuteAsync(RelationDiscoveryJobArgs args)
    {
        // Codex review fix [high] "Tenant context dropped": providers depend on ABP's
        // IMultiTenant ambient filter to scope queries by tenant. Background-job dispatchers
        // don't always restore CurrentTenant from job args (depends on dispatcher config and
        // distributed-event bus). Explicit Change(args.TenantId) makes this deterministic
        // regardless of dispatch path.
        using (_currentTenant.Change(args.TenantId))
        {
            await ExecuteCoreAsync(args);
        }
    }

    protected virtual async Task ExecuteCoreAsync(RelationDiscoveryJobArgs args)
    {
        var totalStopwatch = Stopwatch.StartNew();

        var workItem = await BeginRunAsync(args);
        if (workItem == null)
        {
            // Document hard-deleted; no PipelineRun to mark, but still record run metric for ops.
            totalStopwatch.Stop();
            _telemetry.RecordRun(new RelationDiscoveryRunMetrics
            {
                DocumentId = args.DocumentId,
                Result = RelationDiscoveryRunResult.DocumentMissing,
                TotalDurationMs = totalStopwatch.Elapsed.TotalMilliseconds
            });
            return;
        }

        int createdCount;
        var discoveryStopwatch = Stopwatch.StartNew();
        try
        {
            using var uow = _unitOfWorkManager.Begin(requiresNew: true);
            var created = await _discoveryService.DiscoverAsync(workItem.DocumentId);
            await uow.CompleteAsync();
            createdCount = created.Count;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "RelationDiscovery failed for document {DocumentId}. PipelineRun marked failed; document lifecycle unchanged (non-key pipeline).",
                workItem.DocumentId);
            await FailRunAsync(workItem.DocumentId, workItem.RunId, ex.Message);
            totalStopwatch.Stop();
            discoveryStopwatch.Stop();
            _telemetry.RecordRun(new RelationDiscoveryRunMetrics
            {
                DocumentId = workItem.DocumentId,
                Result = RelationDiscoveryRunResult.Failed,
                FailureReason = ex.GetType().Name,
                DiscoveryDurationMs = discoveryStopwatch.Elapsed.TotalMilliseconds,
                TotalDurationMs = totalStopwatch.Elapsed.TotalMilliseconds
            });
            return;
        }
        discoveryStopwatch.Stop();

        await CompleteRunAsync(workItem.DocumentId, workItem.RunId, createdCount);
        totalStopwatch.Stop();
        _telemetry.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = workItem.DocumentId,
            Result = RelationDiscoveryRunResult.Succeeded,
            CreatedCount = createdCount,
            DiscoveryDurationMs = discoveryStopwatch.Elapsed.TotalMilliseconds,
            TotalDurationMs = totalStopwatch.Elapsed.TotalMilliseconds
        });
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
                "RelationDiscovery: document {DocumentId} no longer exists; dropping job.",
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
            "RelationDiscovery: document {DocumentId} run {RunId} succeeded; created {CreatedCount} AiSuggested relations.",
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

    /// <summary>
    /// Tenant id captured at enqueue time (from <c>DocumentClassifiedEto.TenantId</c> via
    /// <c>RelationDiscoveryEventHandler</c>). The job restores this explicitly via
    /// <c>CurrentTenant.Change</c> in <see cref="RelationDiscoveryBackgroundJob.ExecuteAsync"/>
    /// so providers / repositories see the correct ambient tenant filter regardless of
    /// dispatcher behavior. Codex review fix [high] "Tenant context dropped".
    /// </summary>
    public Guid? TenantId { get; set; }
}
