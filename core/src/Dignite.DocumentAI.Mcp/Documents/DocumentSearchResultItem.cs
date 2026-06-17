using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.DocumentAI.Mcp.Documents;

/// <summary>
/// Single hit returned by the MCP search tool. Thin projection: only system fields needed for
/// document discovery plus the resource URI. Downstream consumers use <see cref="Uri"/> with
/// read_resource to fetch the body, following the channel philosophy of thin payload + pullback.
/// <see cref="Title"/> is user-derived free text and is wrapped with <c>PromptBoundary.WrapField</c>
/// inside the tool.
/// </summary>
public sealed record DocumentSearchResultItem
{
    /// <summary>MCP resource URI for reading the body (<c>docai://documents/{id}</c>).</summary>
    public required string Uri { get; init; }

    public required Guid Id { get; init; }

    /// <summary>Display title, already wrapped with PromptBoundary.</summary>
    public string? Title { get; init; }

    public string? DocumentTypeCode { get; init; }

    public required string LifecycleStatus { get; init; }

    public DateTime CreationTime { get; init; }

    /// <summary>
    /// Whether this document is a <b>container</b> (#346 / #350): a parent bundling several independent
    /// documents that runs no type-bound field extraction itself. When <c>true</c>, an AI client must
    /// <b>not</b> consume this document as a business record — it should instead read the sub-documents
    /// (those whose <see cref="OriginDocumentId"/> equals this <see cref="Id"/>). A system-controlled
    /// boolean, so it is not wrapped with <c>PromptBoundary</c>.
    /// </summary>
    public bool IsContainer { get; init; }

    /// <summary>
    /// Provenance link for a Scenario B sub-document (#306 / #350): the id of the source document this one
    /// was derived from, or <c>null</c> for normally-uploaded documents. A system-controlled id, so it is
    /// not wrapped with <c>PromptBoundary</c>.
    /// </summary>
    public Guid? OriginDocumentId { get; init; }

    /// <summary>
    /// Type-bound field extraction results for this document (LLM-facing). key = field name
    /// (<c>FieldDefinition.Name</c>); value is <see cref="JsonElement"/> and preserves the declared
    /// field type. Structured values such as numbers / booleans pass through raw and serialize as JSON
    /// numbers / booleans, so downstream LLMs infer type from the value without string conversion.
    /// Text-type field values, which are user-derived free text, are wrapped with
    /// <c>PromptBoundary.WrapField</c> and placed back into JSON strings to prevent indirect prompt
    /// injection. null when the document has no extracted fields or all fields are null.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? ExtractedFields { get; init; }
}
