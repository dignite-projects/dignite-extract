namespace Dignite.Paperbase.Chat;

/// <summary>
/// Canonical names of chat-runtime identifiers that cross the core ↔ business-module
/// boundary — currently the LLM-facing names of MAF <c>AIFunction</c> tools that
/// business-module skill SKILL.md instructions need to reference (e.g. for chaining
/// hints like "if empty, try <c>search_paperbase_documents</c>").
///
/// <para>
/// <strong>Why this file lives in Abstractions, not Domain.Shared.</strong>
/// DB-schema constants (<c>MaxTitleLength</c>, <c>MaxMessageLength</c>, etc.) are
/// shaped by the Domain layer and live in <c>Dignite.Paperbase.Domain.Shared</c> so
/// entities and DbContext model-creating extensions can consume them — but business
/// modules cannot reference <c>Domain.Shared</c> without breaking the
/// <c>Abstractions</c>-only dependency rule for modules. The single chat-runtime
/// identifier that business module <c>SKILL.md</c> instructions need is hoisted
/// here so module skills can reference it via raw-string interpolation
/// (<c>$$"""... {{ChatToolNames.SearchPaperbaseDocuments}} ..."""</c>) for
/// compile-time safety. A rename of the underlying AIFunction registration thus
/// becomes a compiler error in every consuming SKILL.md, not a silent prose drift.
/// </para>
///
/// <para>
/// Naming convention: <c>const string</c> only — the compile-time-interpolation use
/// case requires constant expressions. The values are LLM-facing identifiers
/// (lowercase + underscores for tools, kebab-case for skill / script names per the
/// agentskills.io spec).
/// </para>
/// </summary>
public static class ChatToolNames
{
    /// <summary>
    /// Name under which the platform's vector search AIFunction is registered on
    /// the chat agent (<c>ChatAppService.PrepareAgentSetupAsync</c>) and recognised
    /// for grounding classification (<c>ChatTelemetryRecorder.IsVectorSearchTool</c>).
    /// Business-module skill SKILL.md instructions that hint "fall back to vector
    /// search on empty structured result" reference this name.
    /// </summary>
    public const string SearchPaperbaseDocuments = "search_paperbase_documents";
}
