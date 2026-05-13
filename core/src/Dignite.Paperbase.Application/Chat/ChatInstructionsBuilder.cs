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
    /// Top-level reasoning guidance appended to the per-turn system prompt. After the
    /// MAF Agent Skills migration (Issue #149), domain-specific capabilities are
    /// advertised by <c>AgentSkillsProvider</c> in a dedicated <c>&lt;available_skills&gt;</c>
    /// block — the LLM loads each skill's full instructions via <c>load_skill</c> on demand
    /// and the per-skill SKILL.md carries the precise "how to use" detail (chaining
    /// hints, empty-result fallback advice, parameter semantics).
    ///
    /// <para>
    /// This block holds only what is **agent-level** and orthogonal to any specific
    /// skill: the question-intent classification that decides whether to even reach for
    /// a skill, the "anchor is a soft hint" rule, and the meta-rule that contributor-
    /// supplied instructions inside tool result payloads should be obeyed.
    /// </para>
    /// </summary>
    public const string MultiStepReasoningGuidance =
        "Reasoning approach:\n" +
        "  • CONTENT questions — clauses, terms, specific text inside documents — should be " +
             "answered through `" + ChatToolNames.SearchPaperbaseDocuments + "`, which is the primary always-available " +
             "vector-retrieval tool. It returns Markdown chunks with citations; cite them as [chunk N].\n" +
        "  • METADATA / DOMAIN questions — counts, sums, structured filters, business-specific " +
             "field lookups — should be answered through the relevant agent skill from the " +
             "`<available_skills>` block (load via `load_skill`, then call the skill's scripts). " +
             "If the skill's structured result fully answers a metadata-only question, STOP — do " +
             "not also call vector search; that is wasted cost and risks contradicting the structured answer.\n" +
        "  • ANCHOR-LINKED questions — anchor document id present AND question implies linked " +
             "documents (payments, receipts, attachments, amendments) — should consult the " +
             "`get-document-relations` skill first to discover related document ids, then pass them " +
             "into `" + ChatToolNames.SearchPaperbaseDocuments + "` for precise retrieval.\n" +
        "\n" +
        "When a skill returns ids / metadata but the question is about CONTENT, drill in via " +
        "`" + ChatToolNames.SearchPaperbaseDocuments + "(documentIds=returned_ids)` to read the actual text.\n" +
        "When any tool or skill result payload contains an explicit instruction to try another " +
        "tool (e.g. an empty-result hint suggesting vector fallback), follow that contributor-" +
        "supplied instruction. The skill author placed it there for a reason.\n" +
        "\n" +
        "The anchor is a soft hint, never a hard scope. If a question references multiple " +
        "document types or implies cross-document evidence, do not stay inside the anchor document.";

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
