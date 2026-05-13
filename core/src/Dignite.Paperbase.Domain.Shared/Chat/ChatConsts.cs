namespace Dignite.Paperbase.Chat;

/// <summary>
/// DB schema / persistence constants for the chat aggregates. Consumed by
/// <c>ChatConversation</c> / <c>ChatMessage</c> domain entities and by the EF
/// model-creating extensions in <c>PaperbaseDbContextModelCreatingExtensions</c>.
///
/// <para>
/// LLM-runtime identifiers (tool names that business-module skills must reference
/// from their SKILL.md instructions) live in
/// <see cref="ChatToolNames"/> in <c>Dignite.Paperbase.Abstractions</c> — business
/// modules can reach Abstractions but not <c>Domain.Shared</c>, so the cross-boundary
/// names must sit there.
/// </para>
/// </summary>
public static class ChatConsts
{
    public static int MaxMessageLength { get; set; } = 4000;
    public static int MaxTitleLength { get; set; } = 200;
    public static int MaxCitationsJsonLength { get; set; } = 8192;
}
