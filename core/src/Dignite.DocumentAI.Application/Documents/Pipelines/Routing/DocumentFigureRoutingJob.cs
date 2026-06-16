using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents.DocumentTypes;
using Dignite.DocumentAI.Documents.Pipelines.Classification;
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

namespace Dignite.DocumentAI.Documents.Pipelines.Routing;

/// <summary>
/// Scenario B sub-document routing (#306). Consumes the <see cref="DocumentFigure"/> candidates that the
/// text-extraction job persisted for a source document and decides, per figure, whether the figure is itself
/// a document — by classifying its OCR transcription against the source's tenant document-type layer and
/// comparing against the matched type's <c>ConfidenceThreshold</c> (the same per-type Ready-gate bar). A figure
/// that clears the bar is spawned as its own derived <see cref="Document"/> (its crop copied to an independent
/// blob, back-reference <c>OriginDocumentId</c> / <c>OriginFigureKey</c> set), which then runs the full normal
/// pipeline; a figure that does not is marked <see cref="DocumentFigureStatus.NotADocument"/> and its candidate
/// crop is deleted. Figures that remain inline in the source Markdown either way (#301).
/// <para>
/// <b>Resumable + idempotent.</b> Only <see cref="DocumentFigureStatus.Pending"/> candidates are processed, and
/// each figure's evaluation commits atomically with its status change, so a crash resumes the remaining
/// candidates without re-paying the gate classification or duplicate-spawning. The unique
/// <c>(OriginDocumentId, OriginFigureKey)</c> index on <see cref="Document"/> is the final backstop against a
/// concurrent double-route. Per-figure failures are isolated: one bad figure is logged and left
/// <see cref="DocumentFigureStatus.Pending"/> for a later retry without blocking the others.
/// </para>
/// <para>
/// <b>UoW discipline</b> (background-jobs.md): the gate LLM call and blob IO run outside any UoW; only the
/// derived-document insert + figure status change + derived-pipeline enqueue run inside a short UoW.
/// </para>
/// </summary>
[BackgroundJobName("DocumentAI.DocumentFigureRouting")]
public class DocumentFigureRoutingJob
    : AsyncBackgroundJob<DocumentFigureRoutingJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentFigure, Guid> _figureRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly DocumentClassificationWorkflow _classificationWorkflow;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IBlobContainer<DocumentAIDocumentContainer> _blobContainer;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly ICurrentTenant _currentTenant;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly DocumentAIBehaviorOptions _behaviorOptions;
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public DocumentFigureRoutingJob(
        IDocumentRepository documentRepository,
        IRepository<DocumentFigure, Guid> figureRepository,
        IDocumentTypeRepository documentTypeRepository,
        DocumentClassificationWorkflow classificationWorkflow,
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
        _figureRepository = figureRepository;
        _documentTypeRepository = documentTypeRepository;
        _classificationWorkflow = classificationWorkflow;
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

    public override async Task ExecuteAsync(DocumentFigureRoutingJobArgs args)
    {
        var cancellationToken = _cancellationTokenProvider.Token;

        var workItem = await LoadAsync(args.SourceDocumentId);
        if (workItem is null || workItem.PendingFigures.Count == 0)
        {
            return;
        }

        if (workItem.CandidateTypes.Count == 0)
        {
            // No document types in the source's layer -> nothing can be classified as a document. Mark every
            // candidate NotADocument (and reclaim its crop) so the job does not re-run them forever.
            Logger.LogDebug(
                "Figure routing for source {SourceDocumentId}: no candidate document types; rejecting {Count} figure(s).",
                args.SourceDocumentId, workItem.PendingFigures.Count);
            foreach (var figure in workItem.PendingFigures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SafeRejectAsync(workItem, figure);
            }

            return;
        }

        foreach (var figure in workItem.PendingFigures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RouteFigureAsync(workItem, figure, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-figure isolation: a single bad figure (gate error, blob fault, transient DB error) must
                // not block the rest. Leave it Pending so a later job retry re-attempts it.
                Logger.LogWarning(ex,
                    "Routing figure {FigureId} of source {SourceDocumentId} failed; leaving it Pending for retry.",
                    figure.FigureId, args.SourceDocumentId);
            }
        }
    }

    /// <summary>Load phase (short UoW): snapshot the source's tenant + uploader, candidate types, and pending figures.</summary>
    protected virtual async Task<RoutingWorkItem?> LoadAsync(Guid sourceDocumentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var source = await _documentRepository.FindAsync(sourceDocumentId, includeDetails: false);
        if (source is null)
        {
            return null;
        }

        List<DocumentType> candidateTypes;
        using (_currentTenant.Change(source.TenantId))
        {
            var visible = await _documentTypeRepository.GetListAsync();
            candidateTypes = visible
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.TypeCode)
                .Take(_behaviorOptions.MaxDocumentTypesInClassificationPrompt)
                .ToList();
        }

        var pending = await _figureRepository.GetListAsync(
            f => f.SourceDocumentId == sourceDocumentId && f.Status == DocumentFigureStatus.Pending);

        var figures = pending
            .Select(f => new FigureSnapshot(f.Id, f.ContentHash, f.CropBlobName, f.ContentType, f.PageNumber, f.Transcription))
            .ToList();

        await uow.CompleteAsync();

        return new RoutingWorkItem(
            sourceDocumentId, source.TenantId, source.FileOrigin.UploadedByUserName, candidateTypes, figures);
    }

    protected virtual async Task RouteFigureAsync(
        RoutingWorkItem workItem, FigureSnapshot figure, CancellationToken cancellationToken)
    {
        // Gate (external, no UoW): classify the figure transcription against the source's tenant type layer.
        DocumentClassificationOutcome outcome;
        using (_currentTenant.Change(workItem.TenantId))
        {
            outcome = await _classificationWorkflow.RunAsync(workItem.CandidateTypes, figure.Transcription, cancellationToken);
        }

        // Match the winning type from the already-loaded candidates (same set the classifier chose from), and
        // gate on that type's own ConfidenceThreshold — the figure must read as a confident document of a known
        // type to become one, otherwise it stays inline-only.
        var matched = string.IsNullOrEmpty(outcome.TypeCode)
            ? null
            : workItem.CandidateTypes.FirstOrDefault(t => t.TypeCode == outcome.TypeCode);
        var isDocument = matched != null && outcome.ConfidenceScore >= matched.ConfidenceThreshold;

        if (!isDocument)
        {
            await SafeRejectAsync(workItem, figure);
            return;
        }

        await SpawnDerivedDocumentAsync(workItem, figure, cancellationToken);
    }

    /// <summary>Reject path: mark the candidate NotADocument (short UoW), then reclaim its crop blob (external, fail-open).</summary>
    private async Task SafeRejectAsync(RoutingWorkItem workItem, FigureSnapshot figure)
    {
        using (_currentTenant.Change(workItem.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var entity = await _figureRepository.FindAsync(figure.FigureId);
            if (entity is null || entity.Status != DocumentFigureStatus.Pending)
            {
                return;
            }

            entity.MarkNotADocument();
            await _figureRepository.UpdateAsync(entity);
            await uow.CompleteAsync();
        }

        await TryDeleteBlobAsync(figure.CropBlobName);
    }

    private async Task SpawnDerivedDocumentAsync(
        RoutingWorkItem workItem, FigureSnapshot figure, CancellationToken cancellationToken)
    {
        // External: copy the candidate crop into an independent, derived-document-owned blob so the derived
        // document outlives the source (the source's permanent delete reclaims the candidate crop, not this copy).
        byte[] cropBytes;
        await using (var cropStream = await _blobContainer.GetAsync(figure.CropBlobName))
        using (var buffer = new MemoryStream())
        {
            await cropStream.CopyToAsync(buffer, cancellationToken);
            cropBytes = buffer.ToArray();
        }

        var extension = ImageExtensionForContentType(figure.ContentType);
        var derivedBlobName = _guidGenerator.Create().ToString("N") + extension;
        using (var saveStream = new MemoryStream(cropBytes, writable: false))
        {
            await _blobContainer.SaveAsync(derivedBlobName, saveStream, overrideExisting: true, cancellationToken);
        }

        var derivedDocumentId = _guidGenerator.Create();

        try
        {
            await CommitSpawnAsync(workItem, figure, derivedBlobName, derivedDocumentId, extension, cropBytes.LongLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The spawn UoW failed and rolled back, so the copied blob references no committed document — reclaim
            // it, then rethrow so the per-figure handler leaves the figure Pending for a later retry (which writes
            // a fresh copy). Mirrors the text-extraction job's failed-completion crop cleanup. The
            // already-routed / concurrent-double-spawn paths inside CommitSpawnAsync delete the blob themselves
            // and return without throwing, so they are not double-deleted here.
            await TryDeleteBlobAsync(derivedBlobName);
            throw;
        }
    }

    /// <summary>
    /// Complete phase (short UoW): inserts the derived document, marks the figure Spawned, publishes
    /// <c>DocumentUploadedEto</c>, and queues the derived document's pipeline — all atomically. Returns early
    /// (deleting the orphan copied blob) when the candidate is no longer Pending or a concurrent route already
    /// spawned it; any other failure rolls back and propagates to the caller's orphan-blob cleanup.
    /// </summary>
    private async Task CommitSpawnAsync(
        RoutingWorkItem workItem, FigureSnapshot figure, string derivedBlobName, Guid derivedDocumentId,
        string extension, long fileSize)
    {
        using (_currentTenant.Change(workItem.TenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var entity = await _figureRepository.FindAsync(figure.FigureId);
            if (entity is null || entity.Status != DocumentFigureStatus.Pending)
            {
                // Another run already routed it (or it was removed). Drop our orphan copied blob and stop.
                await TryDeleteBlobAsync(derivedBlobName);
                return;
            }

            var shortHash = figure.ContentHash.Length > 8 ? figure.ContentHash[..8] : figure.ContentHash;
            var fileOrigin = new FileOrigin(
                blobName: derivedBlobName,
                uploadedByUserName: workItem.SourceUploadedByUserName,
                contentType: figure.ContentType,
                contentHash: figure.ContentHash,
                fileSize: fileSize,
                originalFileName: $"figure-{shortHash}{extension}");

            var derived = Document.CreateDerived(
                derivedDocumentId, workItem.TenantId, fileOrigin, workItem.SourceDocumentId, figure.ContentHash);

            try
            {
                await _documentRepository.InsertAsync(derived, autoSave: true);
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                // A concurrent route already spawned this figure's derived document (the unique
                // (OriginDocumentId, OriginFigureKey) index fired). Roll back, drop our orphan blob, and stop;
                // the winning run owns the figure's status.
                Logger.LogInformation(
                    "Figure {FigureId} of source {SourceDocumentId} was already spawned concurrently; skipping.",
                    figure.FigureId, workItem.SourceDocumentId);
                await TryDeleteBlobAsync(derivedBlobName);
                return;
            }

            entity.MarkSpawned(derivedDocumentId);
            await _figureRepository.UpdateAsync(entity);

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

            // Run the derived document through the full normal pipeline. Its text-extraction job seeds Markdown
            // from this figure's transcription instead of re-OCR'ing the crop (see DocumentTextExtractionBackgroundJob).
            await _pipelineJobScheduler.QueueAsync(derived, DocumentAIPipelines.TextExtraction);

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
            Logger.LogWarning(ex, "Failed to delete blob {BlobName} during figure routing cleanup.", blobName);
        }
    }

    /// <summary>Whether the exception is (or wraps) a DB unique-constraint violation, used to detect a concurrent double-spawn.</summary>
    protected virtual bool IsUniqueConstraintViolation(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is Volo.Abp.Data.AbpDbConcurrencyException)
            {
                return true;
            }

            var typeName = current.GetType().Name;
            if (typeName == "DbUpdateException" || typeName == "UniqueConstraintException")
            {
                return true;
            }
        }

        return false;
    }

    private static string ImageExtensionForContentType(string contentType)
        => contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            _ => ".bin"
        };

    /// <summary>Detached snapshot of one pending candidate figure, carried across the per-figure external + UoW phases.</summary>
    protected sealed record FigureSnapshot(
        Guid FigureId, string ContentHash, string CropBlobName, string ContentType, int? PageNumber, string Transcription);

    /// <summary>Per-source routing context loaded once up front: tenant + uploader provenance, candidate types, and pending figures.</summary>
    protected sealed record RoutingWorkItem(
        Guid SourceDocumentId,
        Guid? TenantId,
        string SourceUploadedByUserName,
        IReadOnlyList<DocumentType> CandidateTypes,
        IReadOnlyList<FigureSnapshot> PendingFigures);
}

public class DocumentFigureRoutingJobArgs
{
    /// <summary>The source document whose pending candidate figures should be routed.</summary>
    public Guid SourceDocumentId { get; set; }
}
