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
