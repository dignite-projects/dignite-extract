using System;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Contracts.Dtos;

public class UpdateContractDto
{
    [DynamicStringLength(typeof(ContractConsts), nameof(ContractConsts.MaxTitleLength))]
    public string? Title { get; set; }

    [DynamicStringLength(typeof(ContractConsts), nameof(ContractConsts.MaxContractNumberLength))]
    public string? ContractNumber { get; set; }

    [DynamicStringLength(typeof(ContractConsts), nameof(ContractConsts.MaxPartyNameLength))]
    public string? PartyAName { get; set; }

    [DynamicStringLength(typeof(ContractConsts), nameof(ContractConsts.MaxPartyNameLength))]
    public string? PartyBName { get; set; }

    public DateTime? SignedDate { get; set; }

    public DateTime? EffectiveDate { get; set; }

    public DateTime? ExpirationDate { get; set; }

    public decimal? TotalAmount { get; set; }

    [DynamicStringLength(typeof(ContractConsts), nameof(ContractConsts.MaxCurrencyLength))]
    public string? Currency { get; set; }

    public bool? AutoRenewal { get; set; }

    public int? TerminationNoticeDays { get; set; }

    [DynamicStringLength(typeof(ContractConsts), nameof(ContractConsts.MaxGoverningLawLength))]
    public string? GoverningLaw { get; set; }

    [DynamicStringLength(typeof(ContractConsts), nameof(ContractConsts.MaxSummaryLength))]
    public string? Summary { get; set; }
}
