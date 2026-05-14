using System;
using System.Globalization;
using Dignite.Paperbase.Contracts;

namespace Dignite.Paperbase.Contracts.Extraction;

/// <summary>
/// LLM-boundary adapter: converts the LLM's <see cref="ContractExtractionResult"/>
/// shape into a domain-shaped <see cref="ContractFields"/>. Date-string parsing and
/// confidence normalization are LLM concerns and live here, not in the aggregate.
/// </summary>
public static class ContractExtractionResultExtensions
{
    public static ContractFields ToContractFields(this ContractExtractionResult result)
    {
        return new ContractFields
        {
            Title = result.Title,
            ContractNumber = result.ContractNumber,
            PartyAName = result.PartyAName,
            PartyBName = result.PartyBName,
            SignedDate = ParseDate(result.SignedDate),
            EffectiveDate = ParseDate(result.EffectiveDate),
            ExpirationDate = ParseDate(result.ExpirationDate),
            TotalAmount = result.TotalAmount,
            Currency = string.IsNullOrEmpty(result.Currency) ? "JPY" : result.Currency,
            AutoRenewal = result.AutoRenewal,
            TerminationNoticeDays = result.TerminationNoticeDays,
            GoverningLaw = result.GoverningLaw,
            Summary = result.Summary,
            ExtractionConfidence = NormalizeConfidence(result.ExtractionConfidence)
        };
    }

    private static double? NormalizeConfidence(double? value)
    {
        if (!value.HasValue || value.Value < 0 || value.Value > 1)
        {
            return null;
        }

        return value.Value;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }
}
