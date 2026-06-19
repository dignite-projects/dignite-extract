using System.Threading.Tasks;
using Dignite.Extract.Abstractions.Documents;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Uow;

namespace Dignite.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Unified field extraction EventHandler (field architecture v2). Subscribes to
/// <see cref="DocumentClassifiedEto"/> and cascades field extraction after classification completes.
/// Since #289 step 1, the core extraction action (read field definitions -> LLM -> guardrails ->
/// <c>SetFields</c> -> publish <see cref="FieldsExtractedEto"/>) lives in reusable
/// <see cref="FieldExtractionService"/>. This handler only adapts the event layer: early-return for
/// empty TypeCode and translate event payload into engine input, including stale reclassification
/// event early-return optimization.
/// <para>
/// Cross-tenant guard, in-flight reclassification race guard, and three-stage short-UoW discipline
/// are all centralized in the engine (CLAUDE.md "Security Covenant" +
/// <c>.claude/rules/background-jobs.md</c>). The handler keeps
/// <c>[UnitOfWork(IsDisabled = true)]</c> to disable ambient UoW, letting each engine stage create its
/// own <c>requiresNew</c> short UoW and ensuring external LLM calls are never wrapped in a long
/// transaction.
/// </para>
/// </summary>
public class FieldExtractionEventHandler
    : IDistributedEventHandler<DocumentClassifiedEto>, ITransientDependency
{
    private readonly FieldExtractionService _fieldExtractionService;

    public FieldExtractionEventHandler(FieldExtractionService fieldExtractionService)
    {
        _fieldExtractionService = fieldExtractionService;
    }

    [UnitOfWork(IsDisabled = true)]
    public virtual async Task HandleEventAsync(DocumentClassifiedEto eventData)
    {
        if (string.IsNullOrWhiteSpace(eventData.DocumentTypeCode))
        {
            return;
        }

        // Pass the event TypeCode as a stale reclassification early-return hint. The engine extracts
        // by the Document's current DocumentTypeId (#207).
        await _fieldExtractionService.ExtractAsync(
            eventData.DocumentId,
            eventData.TenantId,
            expectedEventTypeCode: eventData.DocumentTypeCode);
    }
}
