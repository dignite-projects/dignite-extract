using System;
using System.Text;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Issue #100: structured assembly of the per-turn system prompt for
/// <c>ChatAppService</c>. Replaces the ad-hoc string concatenation that grew
/// inside <c>PrepareAgentSetupAsync</c> as boundary rules / anchor hints / multi-step
/// reasoning guidance accumulated. Each segment is rendered on its own line so
/// downstream prompt-cache hashing stays stable when only one segment changes.
/// </summary>
internal static class ChatInstructionsBuilder
{
    /// <summary>
    /// Multi-step / cross-document reasoning guidance appended to the system prompt
    /// of every chat turn. Intent-driven: tells the model WHICH tool family fits the
    /// question (content vs metadata vs anchor-graph).
    ///
    /// <para>
    /// Empirical motivation: with a step-1-then-step-2 chain framing, DeepSeek-V3
    /// reliably picked structured tools (e.g. <c>search_contracts</c>) first, treated
    /// the empty/insufficient result as authoritative, and skipped vector search
    /// because the prompt framed it as "follow up *if cross-document evidence is needed*"
    /// — a conditional the model interpreted strictly. Switching to intent-driven
    /// language reliably routes content questions through <c>search_paperbase_documents</c>.
    /// </para>
    ///
    /// <para>
    /// Issue #148: the previous "structured returned EMPTY → try vector before concluding
    /// 'not found'" line was removed from the system prompt. That decision now lives in
    /// each <c>IChatToolContributor</c> implementation — contributors whose data benefits
    /// from a vector fallback (e.g. <c>ContractChatToolContributor</c>) embed a nudge in
    /// their own empty-result payload; contributors whose data is fully structured (future
    /// invoice / receipt modules) can return a clean empty payload with no nudge. This
    /// keeps fully-structured modules from paying vector cost on questions they can already
    /// answer "no records exist" to.
    /// </para>
    /// </summary>
    public const string MultiStepReasoningGuidance =
        "Tool selection by intent:\n" +
        "  • CONTENT questions (clauses, terms, descriptions, any specific text inside documents) → " +
             "call search_paperbase_documents directly. Primary content retrieval tool. " +
             "Structured tools like search_contracts only expose fixed metadata (number, parties, " +
             "amount, dates) and cannot answer content-level questions.\n" +
        "  • METADATA-ONLY questions (contract count, total amount, list by party / date / amount range) → " +
             "use the structured tool that matches (search_contracts, get_contract_aggregate, " +
             "get_contract_detail). If the result fully answers the question, STOP — do not call " +
             "vector search on top. That's wasted cost and risks contradicting the structured answer.\n" +
        "  • ANCHOR-LINKED questions (anchor document id present AND question implies linked documents " +
             "— payments, receipts, attachments, amendments) → call get_document_relations(anchorDocumentId) " +
             "first to discover related document ids, then pass them into " +
             "search_paperbase_documents(documentIds=[...]) for precise retrieval.\n" +
        "\n" +
        "When a structured tool returned ids / metadata but the question is about CONTENT " +
        "(clauses, terms, specifics) → drill in via " +
        "search_paperbase_documents(documentIds=returned_ids) to read the actual text. " +
        "When a structured tool's own result payload contains an instruction to try vector " +
        "search (e.g. an empty-result hint), follow that contributor-supplied instruction. " +
        "Do NOT add a vector follow-up when the structured result fully answers a metadata-only question.\n" +
        "\n" +
        "Chaining patterns:\n" +
        "  • Narrow-then-content: search_contracts(filter) → search_paperbase_documents(documentIds=returned_ids).\n" +
        "  • Pure content: search_paperbase_documents directly (no structured pre-step needed).\n" +
        "  • Reconciliation: get_document_relations(anchorId) → " +
             "search_paperbase_documents(documentIds=returned_ids, documentTypeCode='receipt.general').\n" +
        "\n" +
        "The anchor is a soft hint, never a hard scope. If a question references multiple document " +
        "types or implies cross-document evidence, do not stay inside the anchor document.";

    public static string Build(
        string baseInstructions,
        string boundaryRule,
        string? anchorContext,
        string multiStepGuidance)
    {
        if (baseInstructions is null) throw new ArgumentNullException(nameof(baseInstructions));
        if (boundaryRule is null) throw new ArgumentNullException(nameof(boundaryRule));
        if (multiStepGuidance is null) throw new ArgumentNullException(nameof(multiStepGuidance));

        var sb = new StringBuilder(
            capacity: baseInstructions.Length + boundaryRule.Length + (anchorContext?.Length ?? 0) + multiStepGuidance.Length + 16);

        sb.Append(baseInstructions);
        AppendSection(sb, boundaryRule);
        if (!string.IsNullOrEmpty(anchorContext))
        {
            AppendSection(sb, anchorContext);
        }
        AppendSection(sb, multiStepGuidance);

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string segment)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
        sb.Append('\n');
        sb.Append(segment);
    }
}
