using System;

namespace Dignite.Paperbase.Contracts;

/// <summary>
/// Plain data carrier for the editable / extractable fields on a <see cref="Contract"/>.
/// Used as input to the aggregate's create / update / correct methods and as a snapshot
/// shape on <see cref="ContractExtractionCorrectionContext"/>.
///
/// <para>
/// Intentionally narrow: no <c>NeedsReview</c>, no <c>ReviewStatus</c>, no LLM-specific
/// fields. Review state is derived inside the aggregate; LLM concerns stay at the
/// extraction boundary (<see cref="ContractExtractionResult"/>).
/// </para>
/// </summary>
public class ContractFields
{
    public string? Title { get; set; }

    public string? ContractNumber { get; set; }

    public string? PartyAName { get; set; }

    public string? PartyBName { get; set; }

    public DateTime? SignedDate { get; set; }

    public DateTime? EffectiveDate { get; set; }

    public DateTime? ExpirationDate { get; set; }

    public decimal? TotalAmount { get; set; }

    public string? Currency { get; set; }

    public bool? AutoRenewal { get; set; }

    public int? TerminationNoticeDays { get; set; }

    public string? GoverningLaw { get; set; }

    public string? Summary { get; set; }

    public double? ExtractionConfidence { get; set; }
}
