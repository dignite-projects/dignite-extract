using System.Collections.Generic;

namespace Dignite.Extract.Documents;

/// <summary>
/// Structured detail for one review reason (#284), rendered by the operator UI. <b>Server computes /
/// client only renders</b>: <see cref="IsBlocking"/> is filled by the server from
/// <c>ReviewReasonPolicy</c>, so clients do not re-decide which reasons are blocking.
/// <see cref="MissingFieldNames"/> is populated only for
/// <see cref="DocumentReviewReasons.MissingRequiredFields"/> and contains display names of missing
/// required fields.
/// </summary>
public class ReviewReasonDetailDto
{
    /// <summary>Single reason represented by this detail, not a combination.</summary>
    public DocumentReviewReasons Reason { get; set; }

    /// <summary>Whether this blocks downstream use (Ready gate). Projected by the server from policy and used directly by clients.</summary>
    public bool IsBlocking { get; set; }

    /// <summary>Display names of missing required fields, non-empty only for MissingRequiredFields.</summary>
    public List<string>? MissingFieldNames { get; set; }
}
