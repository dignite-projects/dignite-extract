using System;
using System.Collections.Generic;
using System.Globalization;
using Dignite.Paperbase.Abstractions.Agents;
using Dignite.Paperbase.Contracts.Contracts;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Contracts.EventHandlers;

/// <summary>
/// Validates the raw <see cref="ContractExtractionResult"/> produced by the LLM before
/// it is converted to <see cref="ContractFields"/> and written to the aggregate.
///
/// <para>
/// Rules are split into <strong>Errors</strong> (trigger an LLM retry through
/// <see cref="StructuredExtractionRetryMiddleware"/>) and <strong>Warnings</strong>
/// (do not trigger a retry, used by the EventHandler to mark the record for review).
/// Error messages are sent back to the LLM verbatim — they must reference the offending
/// value and the rule that was violated so the model can self-correct.
/// </para>
/// </summary>
public class ContractExtractionValidator :
    IExtractionValidator<ContractExtractionResult>,
    ITransientDependency
{
    public virtual ExtractionValidationResult Validate(ContractExtractionResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var errors = new List<string>();
        var warnings = new List<string>();

        ValidateAmount(result, errors);
        ValidateCurrency(result, errors);
        var dateValidities = ValidateDates(result, errors);
        ValidateDateOrdering(result, dateValidities, errors);
        ValidateAtLeastOneParty(result, errors);
        ValidateTerminationNoticeDays(result, warnings);
        ValidateConfidence(result, warnings);

        return errors.Count == 0
            ? new ExtractionValidationResult(true, Array.Empty<string>(), warnings)
            : new ExtractionValidationResult(false, errors, warnings);
    }

    protected virtual void ValidateAmount(
        ContractExtractionResult result, List<string> errors)
    {
        if (result.TotalAmount.HasValue && result.TotalAmount.Value < 0)
        {
            errors.Add(
                $"TotalAmount must be non-negative; got {result.TotalAmount.Value}. " +
                "If the contract has no monetary value, set TotalAmount to null.");
        }
    }

    protected virtual void ValidateCurrency(
        ContractExtractionResult result, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(result.Currency))
        {
            return;
        }

        var code = result.Currency.Trim();
        if (!IsLikelyIso4217Code(code))
        {
            errors.Add(
                $"Currency must be an ISO 4217 three-letter code (e.g. JPY, USD, EUR); got '{result.Currency}'. " +
                "If currency cannot be determined from the document, set Currency to null and the system will default it to JPY.");
        }
    }

    private static bool IsLikelyIso4217Code(string code)
    {
        if (code.Length != 3)
        {
            return false;
        }
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] < 'A' || code[i] > 'Z')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Returns whether each of the three date fields, if non-empty, parses to a valid ISO 8601 date.
    /// Used by <see cref="ValidateDateOrdering"/> to avoid comparing dates that already failed to parse.
    /// </summary>
    protected virtual DateValidities ValidateDates(
        ContractExtractionResult result, List<string> errors)
    {
        return new DateValidities(
            CheckDate(nameof(result.SignedDate), result.SignedDate, errors),
            CheckDate(nameof(result.EffectiveDate), result.EffectiveDate, errors),
            CheckDate(nameof(result.ExpirationDate), result.ExpirationDate, errors));
    }

    private static bool CheckDate(string name, string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false; // null is valid but nothing to compare against later
        }

        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            return true;
        }

        errors.Add(
            $"{name} '{value}' is not a valid ISO 8601 date (expected yyyy-MM-dd). " +
            "If the date is unknown, set the field to null.");
        return false;
    }

    protected virtual void ValidateDateOrdering(
        ContractExtractionResult result,
        DateValidities validities,
        List<string> errors)
    {
        if (!validities.EffectiveValid || !validities.ExpirationValid)
        {
            return;
        }

        // Safe to parse — we already validated both strings above.
        var effective = DateTime.ParseExact(result.EffectiveDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expiration = DateTime.ParseExact(result.ExpirationDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (effective > expiration)
        {
            errors.Add(
                $"EffectiveDate ({result.EffectiveDate}) must be on or before ExpirationDate ({result.ExpirationDate}). " +
                "Verify which document field maps to which — swap if needed.");
        }
    }

    protected virtual void ValidateAtLeastOneParty(
        ContractExtractionResult result, List<string> errors)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(result.Title);
        var hasA = !string.IsNullOrWhiteSpace(result.PartyAName);
        var hasB = !string.IsNullOrWhiteSpace(result.PartyBName);
        var hasCounterparty = !string.IsNullOrWhiteSpace(result.CounterpartyName);

        if (!hasTitle && !hasA && !hasB && !hasCounterparty)
        {
            errors.Add(
                "At least one of Title / PartyAName / PartyBName / CounterpartyName must be present. " +
                "If the document is a contract, find any identifying name (party, agreement title, or both) " +
                "and fill the most accurate field.");
        }
    }

    protected virtual void ValidateTerminationNoticeDays(
        ContractExtractionResult result, List<string> warnings)
    {
        if (!result.TerminationNoticeDays.HasValue)
        {
            return;
        }
        var days = result.TerminationNoticeDays.Value;
        if (days < 0 || days > 365)
        {
            warnings.Add(
                $"TerminationNoticeDays {days} is outside the typical [0, 365] range — " +
                "kept as-is but flagged for review.");
        }
    }

    protected virtual void ValidateConfidence(
        ContractExtractionResult result, List<string> warnings)
    {
        if (result.ExtractionConfidence is < 0.5)
        {
            warnings.Add(
                $"ExtractionConfidence {result.ExtractionConfidence:F2} is low — " +
                "record kept but routed to manual review.");
        }
    }

    /// <summary>Helper passing parsed-date validity flags between the date-format check and the ordering check.</summary>
    protected readonly record struct DateValidities(bool SignedValid, bool EffectiveValid, bool ExpirationValid);
}
