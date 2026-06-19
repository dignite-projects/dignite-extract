using System.Collections.Generic;

namespace Dignite.Extract.Documents.Pipelines;

/// <summary>
/// Pipeline identifier constants defined by the core layer.
/// Business modules may register custom PipelineCode values, with "{moduleCode}." as the recommended
/// naming prefix, but they are not included in lifecycle derivation.
/// <para>
/// <see cref="Parse"/> / <see cref="Classification"/> must be <c>const</c>: they are
/// persisted to the <c>DocumentPipelineRun.PipelineCode</c> column, passed across JobArgs / ETO
/// payloads, and used as constant patterns in the <c>DocumentPipelineJobScheduler</c> switch
/// expression. Any runtime mutation would make historical DB data write under the old code while new
/// code reads under the new code, breaking dispatch logic.
/// </para>
/// </summary>
public static class ExtractPipelines
{
    /// <summary>Text extraction (OCR or native extraction). Key pipeline.</summary>
    public const string Parse = "text-extraction";

    /// <summary>Document classification (rule matching / AI). Key pipeline.</summary>
    public const string Classification = "classification";

    /// <summary>
    /// Type-bound field extraction (#289). <b>Non-key pipeline, lifecycle-neutral</b>: intentionally
    /// excluded from <see cref="KeyPipelines"/>, so
    /// <c>DocumentPipelineRunManager.DeriveLifecycleAsync</c> does not derive
    /// <c>LifecycleStatus</c> from it, and field re-extraction does not move an already Ready document
    /// back to Processing. It reuses <c>DocumentPipelineRun</c> only for observability + retry and does
    /// not participate in the Ready gate. Field-extraction cascade is still driven by the
    /// classification-completed event (<c>FieldExtractionEventHandler</c>); this pipeline is the
    /// independent trigger for on-demand / bulk field re-extraction.
    /// </summary>
    public const string FieldExtraction = "field-extraction";

    /// <summary>Pipelines considered "key" during lifecycle derivation. <see cref="FieldExtraction"/> is intentionally excluded because it is lifecycle-neutral.</summary>
    public static readonly IReadOnlyCollection<string> KeyPipelines = new[]
    {
        Parse,
        Classification
    };

    /// <summary>
    /// Pipelines users can manually retry.
    /// Custom pipelines from business modules are not exposed for retry through this API.
    /// </summary>
    public static readonly IReadOnlyCollection<string> RetryablePipelines = new[]
    {
        Parse,
        Classification,
        FieldExtraction
    };
}
