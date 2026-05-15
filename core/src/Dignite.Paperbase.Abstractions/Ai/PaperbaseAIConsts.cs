namespace Dignite.Paperbase.Ai;

/// <summary>
/// Public constants used by the host wiring (<c>PaperbaseHostModule.ConfigureAI</c>) and
/// every service across core + business modules that consumes a keyed AI client via DI.
///
/// <para>
/// Lives in <c>Dignite.Paperbase.Abstractions</c> so business modules (whose Domain layer
/// references only Abstractions, not Application) can also inject these keyed clients.
/// See <c>docs/ai-provider.md</c> for the keyed-clients table and what each is for.
/// </para>
/// </summary>
public static class PaperbaseAIConsts
{
    /// <summary>
    /// DI key for the summarizer <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// used by <c>SummarizationCompactionStrategy</c>. Host registers under this key;
    /// the application layer pulls via <c>[FromKeyedServices(...)]</c>.
    /// Hosts that don't configure a separate summarizer model fall back to the same
    /// underlying chat model — the application layer must accept that arrangement.
    /// </summary>
    public const string SummarizerChatClientKey = "paperbase-summarizer";

    /// <summary>
    /// DI key for the conversation-title-generator <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// used by <c>ChatAppService.TryGenerateAndApplyTitleAsync</c> and by
    /// <c>DocumentTextExtractionBackgroundJob.TryGenerateTitleAsync</c>. Like the summarizer
    /// key, this is a single-shot text-completion path: no tools, no distributed cache
    /// (each prompt is unique), no FunctionInvocation wrapper. Splitting it off from the
    /// main chat client keeps trace structure honest (no phantom <c>orchestrate_tools</c>
    /// spans around a tool-free call) and lets hosts pick a cheaper / faster model for
    /// the title side without dragging the main chat down.
    /// </summary>
    public const string TitleGeneratorChatClientKey = "paperbase-title-generator";

    /// <summary>
    /// DI key for the structured-output <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// shared by all single-shot, tool-free, prompt-unique <c>RunAsync&lt;T&gt;</c> call
    /// sites: <c>DocumentClassificationWorkflow</c>, <c>DocumentRerankWorkflow</c>, and
    /// business-module field extractors like <c>ContractDocumentHandler.ExtractFieldsAsync</c>.
    ///
    /// <para>
    /// Same shape as the summarizer + title-generator clients (no FunctionInvocation,
    /// no DistributedCache) — these calls do not invoke tools, their prompts are
    /// document-content-derived (unique per call), and their outputs are schema-bound
    /// by <c>ChatResponseFormat.ForJsonSchema&lt;T&gt;</c>. Wrapping them with
    /// FunctionInvocation just produces phantom <c>orchestrate_tools</c> spans on traces,
    /// and DistributedCache lookups always miss.
    /// </para>
    ///
    /// <para>
    /// Hosts that want per-task model tuning (e.g. small fast model for classification,
    /// stronger model for field extraction) can override <c>ConfigureAI</c> to register
    /// additional per-purpose keyed clients on top of this default consolidation.
    /// </para>
    /// </summary>
    public const string StructuredChatClientKey = "paperbase-structured";
}
