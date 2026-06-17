namespace Dignite.DocumentAI.Ai;

/// <summary>
/// Application-layer behavior knobs for AI workflows (Classification / structured field extraction).
/// Bound to the <c>DocumentAIBehavior</c> configuration section in
/// <see cref="DocumentAIApplicationModule"/>.
/// <para>
/// Provider wiring (endpoint / API key / model ids / prompt-cache middleware) lives in the
/// separate <c>DocumentAI</c> section consumed by the host's <c>ConfigureAI</c> — keep these
/// two concerns disjoint: this class must not grow connection or credential fields.
/// </para>
/// </summary>
public class DocumentAIBehaviorOptions
{
    /// <summary>
    /// Maximum number of candidate types included in the classification prompt. Extra candidates are
    /// truncated after sorting by descending Priority.
    /// </summary>
    public int MaxDocumentTypesInClassificationPrompt { get; set; } = 50;

    /// <summary>
    /// Maximum number of document Markdown characters included in the classification prompt. When
    /// exceeded, the leading prefix is truncated by UTF-16 code units without splitting surrogate
    /// pairs. This applies <b>only to the classification path</b>
    /// (<c>DocumentClassificationWorkflow</c>). Field extraction (<c>FieldExtractionWorkflow</c>)
    /// intentionally feeds the <b>full</b> Markdown without truncation because type-bound fields can
    /// appear anywhere in the document, and tail truncation would silently miss extraction.
    /// </summary>
    public int MaxTextLengthPerExtraction { get; set; } = 8000;

    /// <summary>
    /// Default language for AI interactions. This applies <b>only to the classification path</b>
    /// (DocumentClassificationWorkflow, forcing classification output / reason to this language).
    /// Other LLM paths use their own designed language strategy and <b>do not</b> consume this option:
    /// <list type="bullet">
    ///   <item>Classification: force this language (DefaultLanguage).</item>
    ///   <item>Title: follow the document language; the prompt says "respond in the same language as the document".</item>
    ///   <item>Field values: preserve the document's original wording.</item>
    ///   <item>Slug: force English translation for URL-friendliness.</item>
    /// </list>
    /// This path-specific split is intentional design, not a bug. Confirm the language strategy before
    /// adding a new LLM path.
    /// </summary>
    public string DefaultLanguage { get; set; } = "ja";

    /// <summary>
    /// Maximum Markdown characters sent to the LLM for title generation.
    /// The tail is truncated when exceeded because the beginning of a document usually contains the
    /// title, summary, and other key information.
    /// </summary>
    public int MaxTitleGenerationMarkdownLength { get; set; } = 4000;

    /// <summary>
    /// Maximum number of candidate cabinets included in the "blank cabinet AI fallback" (#265)
    /// prompt. Extra candidates are truncated. Mirrors
    /// <see cref="MaxDocumentTypesInClassificationPrompt"/> because cabinet selection and
    /// classification are both "choose one from a bounded candidate set" tasks.
    /// </summary>
    public int MaxCabinetsInSuggestionPrompt { get; set; } = 50;

    /// <summary>
    /// Abstention threshold for "blank cabinet AI fallback" (#265): when the LLM confidence is below
    /// this value, <b>do not</b> write CabinetId and keep the document uncategorized. Cabinets are a
    /// human organization dimension, so it is better to leave the value blank for later operator
    /// reassignment (#257) than to force a low-confidence cabinet. Markdown truncation reuses
    /// <see cref="MaxTextLengthPerExtraction"/> because cabinet selection, like classification, only
    /// needs the leading document semantics.
    /// </summary>
    public double MinCabinetSuggestionConfidence { get; set; } = 0.6;

    /// <summary>
    /// Maximum number of born-digital sub-documents a single container may be split into (#346). A hard cost +
    /// blast-radius bound: each spawned slice runs its own classification + extraction + Ready, so an
    /// adversarial or pathological container could otherwise fan out unbounded work. When the LLM segmentation
    /// pass proposes more document-slices than this, the container is left with a review signal instead of
    /// spawning them (mirrors the figure path's per-source cap). Does not count non-document slices
    /// (cover / index).
    /// </summary>
    public int MaxSegmentsPerDocument { get; set; } = 50;
}
