using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.DocumentAI.Documents.Pipelines.Segmentation;

/// <summary>
/// Born-digital container segmentation (#346): the generalization of #306 figure routing to text bundles. Enqueued
/// from the classification complete-phase when a container is detected, it splits the container's Markdown into its
/// constituent documents and spawns each as a derived <see cref="Document"/> seeded from its slice — reusing the
/// same derived-document sink as figure routing.
/// <para>
/// <b>Two phases, both resumable + idempotent.</b> Phase A runs the one-shot LLM split (skipped if segment rows
/// already exist, so a retry never re-splits and never produces drifting boundaries); per the locked #346 decision
/// the LLM returns only verbatim markers and <see cref="MarkdownSlicer"/> does the deterministic, verifiable
/// cutting. Phase B spawns a derived document per still-<see cref="DocumentSegmentStatus.Pending"/> segment; a crash
/// resumes only the unfinished ones, and the unique <c>(OriginDocumentId, OriginConstituentKey)</c> index on
/// <see cref="Document"/> is the duplicate-spawn backstop. Per-segment faults are isolated (one bad slice does not
/// block the rest) and surfaced (the job rethrows so ABP retries the remaining Pending slices).
/// </para>
/// <para>
/// <b>Failure is never silent.</b> If the split cannot be trusted (untrusted markers, fewer than two document
/// slices, or more than <see cref="DocumentAIBehaviorOptions.MaxSegmentsPerDocument"/>), the container is flagged
/// <see cref="DocumentReviewReasons.SegmentationIncomplete"/> (non-blocking — it stays Ready) so an operator can
/// split / reclassify it instead of it quietly producing zero sub-documents.
/// </para>
/// <para>
/// <b>UoW discipline</b> (background-jobs.md): the LLM split and blob IO run outside any UoW; only the
/// segment-row inserts and each derived-document insert + status change + pipeline enqueue run inside short UoWs.
/// </para>
/// </summary>
[BackgroundJobName("DocumentAI.DocumentSegmentation")]
public class DocumentSegmentationJob
    : AsyncBackgroundJob<DocumentSegmentationJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly DocumentSegmentationWorkflow _segmentationWorkflow;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly ICurrentTenant _currentTenant;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly DocumentAIBehaviorOptions _behaviorOptions;
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public DocumentSegmentationJob(
        IDocumentRepository documentRepository,
        IRepository<DocumentSegment, Guid> segmentRepository,
        DocumentSegmentationWorkflow segmentationWorkflow,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IBlobContainer<DocumentAIDocumentContainer> blobContainer,
        IDistributedEventBus distributedEventBus,
        ICurrentTenant currentTenant,
        IClock clock,
        IGuidGenerator guidGenerator,
        IUnitOfWorkManager unitOfWorkManager,
        IOptions<DocumentAIBehaviorOptions> behaviorOptions,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        _documentRepository = documentRepository;
        _segmentRepository = segmentRepository;
        _segmentationWorkflow = segmentationWorkflow;
        _pipelineJobScheduler = pipelineJobScheduler;
        _blobContainer = blobContainer;
        _distributedEventBus = distributedEventBus;
        _currentTenant = currentTenant;
        _clock = clock;
        _guidGenerator = guidGenerator;
        _unitOfWorkManager = unitOfWorkManager;
        _behaviorOptions = behaviorOptions.Value;
        _cancellationTokenProvider = cancellationTokenProvider;
    }

    public override async Task ExecuteAsync(DocumentSegmentationJobArgs args)
    {
        var cancellationToken = _cancellationTokenProvider.Token;

        var context = await LoadAsync(args.ContainerDocumentId);
        if (context is null)
        {
            return; // container removed before segmentation ran
        }

        // Phase A: split once. If a prior run already persisted segment rows, skip the LLM (resumable, no re-split).
        if (!context.HasExistingSegments)
        {
            await SplitAndPersistAsync(context, cancellationToken);
        }

        // Phase B: spawn a derived document per still-Pending segment. Re-loaded each run, so a retry processes
        // only the slices not yet spawned.
        var pending = await LoadPendingSegmentsAsync(args.ContainerDocumentId);
        if (pending.Count == 0)
        {
            return;
        }

        var failures = new List<Exception>();
        foreach (var segment in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SpawnWithIsolationAsync(failures, segment, context, cancellationToken);
        }

        if (failures.Count > 0)
        {
            // Surface the faults so ABP reschedules the job; already-spawned segments are terminal and skipped on
            // retry (LoadPendingSegmentsAsync re-reads only the still-Pending ones), so retries never duplicate.
            throw new AggregateException(
                $"Segmentation left {failures.Count} slice(s) of container {args.ContainerDocumentId} Pending; the job will be retried.",
                failures);
        }
    }

    /// <summary>Load phase (short UoW): snapshot the container's tenant + uploader + Markdown, and whether segment rows already exist.</summary>
    protected virtual async Task<SegmentationContext?> LoadAsync(Guid containerDocumentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var container = await _documentRepository.FindAsync(containerDocumentId, includeDetails: false);
        if (container is null)
        {
            return null;
        }

        var hasExistingSegments =
            await _segmentRepository.FirstOrDefaultAsync(s => s.SourceDocumentId == containerDocumentId) is not null;

        await uow.CompleteAsync();

        return new SegmentationContext(
            containerDocumentId,
            container.TenantId,
            container.FileOrigin.UploadedByUserName,
            container.Markdown ?? string.Empty,
            hasExistingSegments);
    }

    /// <summary>
    /// Phase A: one LLM split (external, no UoW) → deterministic slicing → validation → a short UoW that inserts
    /// the segment rows. On any untrusted / out-of-bounds result the container is flagged
    /// <see cref="DocumentReviewReasons.SegmentationIncomplete"/> and no rows are written (so Phase B spawns nothing).
    /// </summary>
    protected virtual async Task SplitAndPersistAsync(SegmentationContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Markdown))
        {
            await MarkSegmentationIncompleteAsync(context, "container has no Markdown to segment");
            return;
        }

        // Gate (external, no UoW): the LLM proposes boundaries; keep the ambient tenant aligned as classification does.
        DocumentSegmentationOutcome outcome;
        using (_currentTenant.Change(context.TenantId))
        {
            outcome = await _segmentationWorkflow.RunAsync(context.Markdown, cancellationToken);
        }

        if (!MarkdownSlicer.TrySlice(context.Markdown, outcome.Boundaries, out var slices))
        {
            await MarkSegmentationIncompleteAsync(context, "the LLM split could not be verified against the Markdown");
            return;
        }

        var documentSliceCount = slices.Count(s => s.IsDocument);
        if (documentSliceCount < 2)
        {
            // Fewer than two document slices means this was not really a multi-document bundle; do not spawn a lone
            // duplicate of the container — let an operator reclassify it to a concrete type.
            await MarkSegmentationIncompleteAsync(context, "fewer than two document slices were identified");
            return;
        }

        if (documentSliceCount > _behaviorOptions.MaxSegmentsPerDocument)
        {
            await MarkSegmentationIncompleteAsync(
                context,
                $"the split produced {documentSliceCount} document slices, over the MaxSegmentsPerDocument limit of {_behaviorOptions.MaxSegmentsPerDocument}");
            return;
        }

        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            // Concurrency guard: another run may have committed segments between LoadAsync and here. Re-check inside
            // the UoW; if so, drop this split and let Phase B spawn from the committed rows (no double-split).
            if (await _segmentRepository.FirstOrDefaultAsync(s => s.SourceDocumentId == context.ContainerId) is not null)
            {
                await uow.CompleteAsync();
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slice in slices)
            {
                var segmentKey = ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(slice.Text));
                // De-dupe identical slices within one container: same text -> same key -> one row (the unique
                // (SourceDocumentId, SegmentKey) index is the final guard).
                if (!seen.Add(segmentKey))
                {
                    continue;
                }

                await _segmentRepository.InsertAsync(new DocumentSegment(
                    _guidGenerator.Create(),
                    context.TenantId,
                    context.ContainerId,
                    segmentKey,
                    slice.Text,
                    slice.Ordinal,
                    // Cover / index / transmittal slices are recorded for audit but never spawned.
                    slice.IsDocument ? DocumentSegmentStatus.Pending : DocumentSegmentStatus.NotADocument));
            }

            await uow.CompleteAsync();
        }
    }

    /// <summary>Phase B reload (short UoW): snapshot the still-Pending segments to spawn.</summary>
    protected virtual async Task<List<PendingSegment>> LoadPendingSegmentsAsync(Guid containerDocumentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var pending = await _segmentRepository.GetListAsync(
            s => s.SourceDocumentId == containerDocumentId && s.Status == DocumentSegmentStatus.Pending);

        var snapshot = pending
            .OrderBy(s => s.Ordinal)
            .Select(s => new PendingSegment(s.Id, s.SegmentKey, s.SliceText, s.Ordinal))
            .ToList();

        await uow.CompleteAsync();

        return snapshot;
    }

    /// <summary>
    /// Runs one segment's spawn with per-segment isolation: a fault is logged and collected (so
    /// <see cref="ExecuteAsync"/> can rethrow and trigger a job retry) instead of aborting the remaining segments.
    /// Cancellation is never collected — it propagates so the job is treated as cancelled, not failed.
    /// </summary>
    private async Task SpawnWithIsolationAsync(
        List<Exception> failures, PendingSegment segment, SegmentationContext context, CancellationToken cancellationToken)
    {
        try
        {
            await SpawnDerivedDocumentAsync(segment, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex,
                "Spawning segment {SegmentId} of container {ContainerId} failed; left Pending for job retry.",
                segment.SegmentId, context.ContainerId);
            failures.Add(ex);
        }
    }

    private async Task SpawnDerivedDocumentAsync(
        PendingSegment segment, SegmentationContext context, CancellationToken cancellationToken)
    {
        // External: write the slice to an independent, derived-document-owned blob so the derived document outlives
        // the container (the container's permanent delete reclaims the container/segment rows, not this blob).
        var sliceBytes = Encoding.UTF8.GetBytes(segment.SliceText);
        var derivedBlobName = _guidGenerator.Create().ToString("N") + ".md";
        using (var saveStream = new MemoryStream(sliceBytes, writable: false))
        {
            await _blobContainer.SaveAsync(derivedBlobName, saveStream, overrideExisting: true, cancellationToken);
        }

        var derivedDocumentId = _guidGenerator.Create();

        try
        {
            await CommitSpawnAsync(segment, context, derivedBlobName, derivedDocumentId, sliceBytes.LongLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The spawn UoW rolled back (a concurrent unique-index collision, or any other fault), so the written
            // blob references no committed document — reclaim it, then rethrow so the per-segment handler records
            // the fault and the job is retried (which writes a fresh blob next time). The one CommitSpawnAsync path
            // that returns without throwing (the segment is already non-Pending) deletes its own orphan blob.
            await TryDeleteBlobAsync(derivedBlobName);
            throw;
        }
    }

    /// <summary>
    /// Complete phase (short UoW): inserts the derived document, marks the segment Spawned, publishes
    /// <c>DocumentUploadedEto</c>, and queues the derived document's pipeline — all atomically. Returns early
    /// (deleting the orphan blob) when the segment is no longer Pending or a concurrent run already spawned it.
    /// </summary>
    private async Task CommitSpawnAsync(
        PendingSegment segment, SegmentationContext context, string derivedBlobName, Guid derivedDocumentId, long fileSize)
    {
        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var entity = await _segmentRepository.FindAsync(segment.SegmentId);
            if (entity is null || entity.Status != DocumentSegmentStatus.Pending)
            {
                // Another run already spawned it (or it was removed). Drop our orphan blob and stop.
                await TryDeleteBlobAsync(derivedBlobName);
                return;
            }

            var shortKey = segment.SegmentKey.Length > 8 ? segment.SegmentKey[..8] : segment.SegmentKey;
            var fileOrigin = new FileOrigin(
                blobName: derivedBlobName,
                uploadedByUserName: context.UploadedByUserName,
                contentType: "text/markdown",
                contentHash: segment.SegmentKey,
                fileSize: fileSize,
                originalFileName: $"segment-{shortKey}.md");

            var derived = Document.CreateDerived(
                derivedDocumentId, context.TenantId, fileOrigin, context.ContainerId, segment.SegmentKey);

            // A concurrent run that already committed this segment's derived document trips the unique
            // (OriginDocumentId, OriginConstituentKey) index here. The failure propagates to
            // SpawnDerivedDocumentAsync, which reclaims this run's orphan blob and rethrows so the job retries; on
            // retry the segment is Spawned and skipped — self-healing, no duplicate.
            await _documentRepository.InsertAsync(derived, autoSave: true);

            entity.MarkSpawned(derivedDocumentId);
            await _segmentRepository.UpdateAsync(entity);

            await _distributedEventBus.PublishAsync(
                new DocumentUploadedEto
                {
                    DocumentId = derived.Id,
                    TenantId = derived.TenantId,
                    EventTime = _clock.Now,
                    FileName = fileOrigin.OriginalFileName,
                    FileSize = fileOrigin.FileSize,
                    ContentType = fileOrigin.ContentType
                });

            // Run the derived document through the full normal pipeline. Its text-extraction job seeds Markdown from
            // this segment's SliceText instead of re-extracting the blob (see DocumentTextExtractionBackgroundJob).
            await _pipelineJobScheduler.QueueAsync(derived, DocumentAIPipelines.TextExtraction);

            await uow.CompleteAsync();
        }
    }

    /// <summary>Flags the container with the non-blocking <see cref="DocumentReviewReasons.SegmentationIncomplete"/> signal (short UoW).</summary>
    private async Task MarkSegmentationIncompleteAsync(SegmentationContext context, string reason)
    {
        Logger.LogWarning(
            "Container {ContainerId} segmentation incomplete ({Reason}); flagging for operator review.",
            context.ContainerId, reason);

        using (_currentTenant.Change(context.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var container = await _documentRepository.FindAsync(context.ContainerId, includeDetails: false);
            if (container is null)
            {
                return;
            }

            container.SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: true);
            await _documentRepository.UpdateAsync(container);
            await uow.CompleteAsync();
        }
    }

    private async Task TryDeleteBlobAsync(string blobName)
    {
        try
        {
            await _blobContainer.DeleteAsync(blobName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete blob {BlobName} during segmentation cleanup.", blobName);
        }
    }

    /// <summary>Per-container segmentation context loaded once up front: tenant + uploader provenance, the Markdown to split, and whether a prior split exists.</summary>
    protected sealed record SegmentationContext(
        Guid ContainerId,
        Guid? TenantId,
        string UploadedByUserName,
        string Markdown,
        bool HasExistingSegments);

    /// <summary>Detached snapshot of one still-Pending segment, carried across the per-segment external + UoW phases.</summary>
    protected sealed record PendingSegment(Guid SegmentId, string SegmentKey, string SliceText, int Ordinal);
}

public class DocumentSegmentationJobArgs
{
    /// <summary>The container document whose Markdown should be split into sub-documents.</summary>
    public Guid ContainerDocumentId { get; set; }
}
