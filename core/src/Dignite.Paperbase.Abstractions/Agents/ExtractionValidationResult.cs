using System.Collections.Generic;

namespace Dignite.Paperbase.Abstractions.Agents;

/// <summary>
/// Outcome of validating a structured extraction result. <see cref="Errors"/> is what the
/// retry middleware feeds back to the LLM verbatim, so messages must be self-contained
/// (include the offending value and the rule that was violated). <see cref="Warnings"/>
/// is informational only — does not trigger a retry; useful for telemetry tags.
/// </summary>
public sealed record ExtractionValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static ExtractionValidationResult Ok() =>
        new(true, System.Array.Empty<string>(), System.Array.Empty<string>());

    public static ExtractionValidationResult Failed(params string[] errors) =>
        new(false, errors, System.Array.Empty<string>());
}
