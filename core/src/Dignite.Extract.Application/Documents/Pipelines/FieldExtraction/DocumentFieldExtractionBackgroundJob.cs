using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Uow;

namespace Dignite.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// <c>field-extraction</c> pipeline background job (#289 step 2): execution unit for on-demand / bulk
/// field re-extraction. Reuses <see cref="DocumentPipelineBackgroundJobBase{TArgs}"/> for the
/// three-stage short UoW pattern plus <see cref="DocumentPipelineRun"/> observability / failure retry,
/// while external LLM extraction delegates to the shared #289 step 1 engine
/// <see cref="FieldExtractionService"/>.
/// <para>
/// <b>Lifecycle-neutral</b>: <see cref="ExtractPipelines.FieldExtraction"/> is not in
/// <see cref="ExtractPipelines.KeyPipelines"/>, so <c>DeriveLifecycleAsync</c> triggered by
/// BeginRun / CompleteRun does not change <c>Document.LifecycleStatus</c>. Ready documents remain
/// Ready after field re-extraction and are not moved back to Processing.
/// </para>
/// <para>
/// Same three stages as the classification job: BeginRun (short UoW creates / resumes run and marks
/// Running), external LLM extraction (no UoW), then CompleteRun (short UoW marks Succeeded). Any
/// exception calls <see cref="DocumentPipelineBackgroundJobBase{TArgs}.FailRunAsync"/> to mark Failed,
/// then rethrows to trigger ABP background job retry.
/// </para>
/// </summary>
[BackgroundJobName("Extract.DocumentFieldExtraction")]
public class DocumentFieldExtractionBackgroundJob
    : DocumentPipelineBackgroundJobBase<DocumentFieldExtractionJobArgs>, ITransientDependency
{
    private readonly FieldExtractionService _fieldExtractionService;

    public DocumentFieldExtractionBackgroundJob(
        IDocumentRepository documentRepository,
        IDocumentPipelineRunRepository runRepository,
        DocumentPipelineRunManager pipelineRunManager,
        DocumentPipelineRunAccessor pipelineRunAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        FieldExtractionService fieldExtractionService)
        : base(documentRepository, runRepository, pipelineRunManager, pipelineRunAccessor, unitOfWorkManager)
    {
        _fieldExtractionService = fieldExtractionService;
    }

    public override async Task ExecuteAsync(DocumentFieldExtractionJobArgs args)
    {
        var (documentId, runId, tenantId) = await BeginRunAsync(args);

        try
        {
            // External LLM extraction. The engine internally performs ICurrentTenant.Change(tenantId)
            // + its own three-stage short UoW flow and is never called inside any existing UoW.
            await _fieldExtractionService.ExtractAsync(documentId, tenantId);
            await CompleteRunAsync(documentId, runId);
        }
        catch (Exception ex)
        {
            await FailRunAsync(documentId, runId, ex.Message, ExtractPipelines.FieldExtraction);
            throw;
        }
    }

    private async Task<(Guid DocumentId, Guid RunId, Guid? TenantId)> BeginRunAsync(DocumentFieldExtractionJobArgs args)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var document = await DocumentRepository.GetAsync(args.DocumentId, includeDetails: false);
        var run = await PipelineRunAccessor.BeginOrStartAsync(
            document, args.PipelineRunId, ExtractPipelines.FieldExtraction);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();

        return (document.Id, run.Id, document.TenantId);
    }

    private async Task CompleteRunAsync(Guid documentId, Guid runId)
    {
        using var uow = UnitOfWorkManager.Begin(requiresNew: true);

        var (document, run) = await LoadDocumentAndRunAsync(documentId, runId, ExtractPipelines.FieldExtraction);
        await PipelineRunManager.CompleteAsync(document, run);
        await DocumentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }
}

public class DocumentFieldExtractionJobArgs
{
    public Guid DocumentId { get; set; }
    public Guid? PipelineRunId { get; set; }
}
