using System;

namespace Dignite.Paperbase.Chat;

/// <summary>
/// Audit / telemetry context attached to the
/// <see cref="Search.DocumentTextSearchAdapter"/>'s <c>search_paperbase_documents</c>
/// AIFunction wrapper. Tenant / user / conversation / anchor identifiers are stamped on
/// every <see cref="Telemetry.ChatToolAuditEntry"/> so the audit trail can correlate
/// per-tool invocations back to the originating chat turn.
///
/// <para>
/// Issue #149: this previously lived in <c>Dignite.Paperbase.Abstractions.Chat</c> as part
/// of the <c>IChatToolContributor</c> public contract for business modules. After
/// migrating business modules to MAF Agent Skills (<see cref="Microsoft.Agents.AI.AgentSkill"/>),
/// it is purely internal to core's <c>search_paperbase_documents</c> audit wrapper —
/// business modules no longer see or implement it.
/// </para>
///
/// <para>
/// Arch review A3: visibility narrowed from <c>public</c> to <c>internal</c> to match
/// actual reach (three consumers, all in <c>Dignite.Paperbase.Application</c>). Test
/// assembly <c>Dignite.Paperbase.Application.Tests</c> sees it via the
/// <c>InternalsVisibleTo</c> declared in the csproj.
/// </para>
/// </summary>
internal sealed class ChatToolContext
{
    /// <summary>
    /// Optional document type hint for audit/telemetry tagging. Issue #100 stopped
    /// pinning this on the conversation; the AppService passes <c>null</c> by default.
    /// </summary>
    public string? DocumentTypeCode { get; init; }

    /// <summary>Tenant of the conversation.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Conversation identifier — used for correlating audit entries to the chat turn.</summary>
    public Guid ConversationId { get; init; }

    /// <summary>
    /// Anchor document the conversation was started from (if any). Audit/telemetry hint —
    /// not a hard scope; the LLM is free to query other documents.
    /// </summary>
    public Guid? DocumentId { get; init; }

    /// <summary>User that initiated the current chat turn.</summary>
    public Guid? UserId { get; init; }
}
