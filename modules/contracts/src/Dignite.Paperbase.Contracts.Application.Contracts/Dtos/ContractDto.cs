using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Contracts.Dtos;

public class ContractDto : AuditedEntityDto<Guid>
{
    public Guid? TenantId { get; set; }

    public Guid DocumentId { get; set; }

    public string DocumentTypeCode { get; set; } = default!;

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

    public ContractStatus Status { get; set; }

    public double? ExtractionConfidence { get; set; }

    public bool NeedsReview { get; set; }

    public ContractReviewStatus ReviewStatus { get; set; }
}
