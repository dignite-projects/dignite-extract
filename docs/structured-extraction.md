# Structured Extraction

Business modules turn classified documents into typed records (Contract, Invoice, …) by asking an LLM to produce a structured JSON object. Paperbase provides a reusable **agent middleware** for validating that output and retrying with feedback when the LLM gets it wrong — backed by a stable telemetry surface so dashboards can answer "how often does this fail?" per rule.

This page covers the contract that every extraction validator follows and how to plug a new one in. The reference implementation is `modules/contracts/.../EventHandlers/ContractExtractionValidator.cs`.

## The pattern at a glance

```
DocumentClassifiedEto
        ↓
ContractDocumentHandler.HandleEventAsync
        ↓
new ChatClientAgent(IChatClient, instructions)
        .WithValidationRetry(IExtractionValidator<T>, ILogger)      ← MAF Agent Middleware
        ↓
agent.RunAsync<T>(markdownText)
        ↓
   ┌────┴────┐
   ↓         ↓
 Valid    Invalid → append feedback message → retry once → return last response
   ↓
write to aggregate
   ↓
ContractsTelemetryRecorder.RecordExtraction(...)  ← final-result snapshot
```

The middleware is in `core/src/Dignite.Paperbase.Abstractions/Agents/StructuredExtractionRetryMiddleware.cs`. It uses MAF 1.x's official agent-builder middleware surface (`agent.AsBuilder().Use(runFunc, runStreamingFunc).Build()`) — the same shape MAF itself uses internally for cross-cutting concerns like `ClientHeadersAgent`. Not an `AIContextProvider`: those inject extra context into the LLM, which is exactly the wrong shape for "produce a clean structured object". See [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md) reverse example A for the failure mode this prevents.

## Why this exists

The previous shape — call the agent once, write whatever came back — had no sensor anywhere on the path. The LLM could return:

- `TotalAmount = -1` (impossible)
- `Currency = "yen"` (not ISO 4217)
- `EffectiveDate = "2030-01-01"`, `ExpirationDate = "2020-01-01"` (inverted)
- Dates as `"2026/04/01"` or `"April 1, 2026"` (silently nulled by the adapter)

…and the record would land in the user-facing UI. The middleware closes that gap by running a domain-defined validator on each attempt and feeding the failures back to the model — Boris Cherny's "give the model a way to verify its work improves quality 2–3×" applied at the structured-output boundary.

## Contracts

### `IExtractionValidator<T>` (in `Dignite.Paperbase.Abstractions/Agents/`)

```csharp
public interface IExtractionValidator<T>
{
    ExtractionValidationResult Validate(T result);
}

public sealed record ExtractionRuleViolation(string RuleCode, string Message);

public sealed record ExtractionValidationResult(
    bool IsValid,
    IReadOnlyList<ExtractionRuleViolation> Errors,
    IReadOnlyList<ExtractionRuleViolation> Warnings);
```

A validator implementation must be:

- **Pure** — no I/O, no logging, no clock reads, no DI dependencies beyond what's strictly needed for the rule logic. The middleware calls it once per attempt; the EventHandler calls it once more for the telemetry snapshot.
- **Registered as `ITransientDependency`** — DI picks it up automatically by the closed generic interface.
- **Exhaustive within one call** — report every violation in one return value. The middleware sends all errors to the LLM in a single feedback message; one retry should be enough to clean up multiple issues.

### `ExtractionRuleViolation` — RuleCode + Message

Two fields, two audiences:

| Field | Audience | Constraints |
|---|---|---|
| `RuleCode` | Metrics / dashboards | Stable across releases, low-cardinality, language-neutral. Form: `Module.Field.Rule`, e.g. `Contracts.TotalAmount.NonNegative`. Once a dashboard references it, don't rename. |
| `Message` | The LLM | Natural language, self-contained (include the offending value and what's expected). Sent verbatim to the model on retry. Translate if your prompts are non-English — the model copies the language of the system prompt for its retries. |

The `RuleCode` is what shows up as the `rule` tag on the `paperbase.{module}.extraction.validation_errors` counter (see [observability.md](observability.md)).

### Errors vs Warnings

- **Errors** → trigger a retry. Use for things the LLM can plausibly fix when told what's wrong: malformed dates, out-of-range amounts, missing required fields.
- **Warnings** → never trigger a retry; surfaced to telemetry only. Use for things that are domain-suspicious but accept-as-is: low confidence scores, out-of-typical-range counts. The EventHandler can also read warnings to route the record to manual review.

## Wiring up a new module

Below is the contracts module as the worked example. Roughly four files.

### 1. Validator (in `*.Contracts.Domain/EventHandlers/`)

```csharp
public class ContractExtractionValidator
    : IExtractionValidator<ContractExtractionResult>,
      ITransientDependency
{
    public static class RuleCodes
    {
        public const string TotalAmountNonNegative      = "Contracts.TotalAmount.NonNegative";
        public const string CurrencyIso4217             = "Contracts.Currency.Iso4217";
        public const string DateIso8601                 = "Contracts.Date.Iso8601";
        public const string EffectiveBeforeExpiration   = "Contracts.Date.EffectiveBeforeExpiration";
        public const string AtLeastOneParty             = "Contracts.Party.AtLeastOne";
        public const string TerminationNoticeRange      = "Contracts.TerminationNotice.Range";
        public const string LowConfidence               = "Contracts.Confidence.Low";
    }

    public virtual ExtractionValidationResult Validate(ContractExtractionResult result)
    {
        var errors = new List<ExtractionRuleViolation>();
        var warnings = new List<ExtractionRuleViolation>();

        if (result.TotalAmount is < 0)
        {
            errors.Add(new ExtractionRuleViolation(
                RuleCodes.TotalAmountNonNegative,
                $"TotalAmount must be non-negative; got {result.TotalAmount}. " +
                "If the contract has no monetary value, set TotalAmount to null."));
        }
        // ... rest of the rules ...

        return errors.Count == 0
            ? new ExtractionValidationResult(true, Array.Empty<ExtractionRuleViolation>(), warnings)
            : new ExtractionValidationResult(false, errors, warnings);
    }
}
```

Keep each rule in its own `protected virtual` helper so consumers can override one rule without re-implementing the rest (Paperbase modules are extensibility-first — see [`.claude/rules/module-template.md`](../.claude/rules/module-template.md)).

### 2. Telemetry recorder (in `*.Contracts.Domain/Telemetry/`)

```csharp
public class ContractsTelemetryRecorder : ISingletonDependency
{
    public const string MeterName = "Dignite.Paperbase.Contracts";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>(
        "paperbase.contracts.extraction.attempts",
        description: "...");
    private static readonly Counter<long> ValidationErrors = Meter.CreateCounter<long>(
        "paperbase.contracts.extraction.validation_errors",
        description: "...");
    private static readonly Histogram<double> Confidence = Meter.CreateHistogram<double>(
        "paperbase.contracts.extraction.confidence",
        description: "...");

    public virtual void RecordExtraction(
        string documentTypeCode,
        ExtractionValidationResult finalValidation,
        double? finalConfidence)
    {
        Attempts.Add(1,
            new("document_type_code", documentTypeCode),
            new("success", finalValidation.IsValid));

        foreach (var v in finalValidation.Errors)
        {
            ValidationErrors.Add(1,
                new("rule", v.RuleCode),
                new("document_type_code", documentTypeCode));
        }

        if (finalConfidence.HasValue)
        {
            Confidence.Record(finalConfidence.Value,
                new("document_type_code", documentTypeCode),
                new("success", finalValidation.IsValid));
        }
    }
}
```

The host-side OTel pipeline picks the Meter up through `AddMeter("Dignite.Paperbase.*")` — no host change required for a new module, as long as the Meter name follows the naming convention.

### 3. EventHandler wiring (in `*.Contracts.Domain/EventHandlers/`)

```csharp
public class ContractDocumentHandler : IDistributedEventHandler<DocumentClassifiedEto>, ITransientDependency
{
    public ContractDocumentHandler(
        // ... existing deps ...
        IExtractionValidator<ContractExtractionResult> extractionValidator,
        ContractsTelemetryRecorder telemetry,
        ILogger<ContractDocumentHandler>? logger = null)
    {
        // ...
    }

    public virtual async Task HandleEventAsync(DocumentClassifiedEto eventData)
    {
        using (_currentTenant.Change(eventData.TenantId))
        {
            var extraction = await ExtractFieldsAsync(eventData.Markdown ?? "", eventData.DocumentTypeCode);

            // Final-result snapshot for telemetry. Validator is pure so re-running
            // here is essentially free vs. the LLM cost above.
            var finalValidation = _extractionValidator.Validate(extraction);
            _telemetry.RecordExtraction(
                eventData.DocumentTypeCode,
                finalValidation,
                extraction.ExtractionConfidence);

            // ... persist to aggregate ...
        }
    }

    protected virtual async Task<ContractExtractionResult> ExtractFieldsAsync(string text, string typeCode)
    {
        var instructions = await BuildExtractionInstructionsAsync(typeCode);
        var agent = new ChatClientAgent(_chatClient, instructions: instructions)
            .WithValidationRetry(_extractionValidator, _logger);   // ← one-line plug-in

        var run = await agent.RunAsync<ContractExtractionResult>(text);
        return run.Result ?? new ContractExtractionResult();
    }
}
```

### 4. Domain guard — last line of defense

The middleware is the **fast** sensor; the aggregate is the **last** one. Even if a future code path (admin tool, data import, hand-rolled migration) skips the middleware, the aggregate must still refuse a bad write:

```csharp
public class Contract : AuditedAggregateRoot<Guid>, IMultiTenant
{
    protected virtual void ApplyFields(ContractFields fields)
    {
        ValidateFields(fields);   // ← invoked before any state mutation
        Title = fields.Title;
        // ...
    }

    protected virtual void ValidateFields(ContractFields fields)
    {
        if (fields.TotalAmount is < 0)
            throw new BusinessException("Contracts:InvalidContractField")
                .WithData("Field", nameof(fields.TotalAmount))
                .WithData("Value", fields.TotalAmount);

        if (fields.EffectiveDate.HasValue && fields.ExpirationDate.HasValue &&
            fields.EffectiveDate > fields.ExpirationDate)
        {
            throw new BusinessException("Contracts:InvalidContractField")
                .WithData("Field", nameof(fields.EffectiveDate));
        }
    }
}
```

The middleware-validator-domain-guard layering is intentional: each layer protects against a different failure mode. The middleware fixes typical LLM mistakes via retry. The domain guard refuses to persist anything that's still wrong after the middleware finishes — including data that never went through the middleware in the first place.

## Tuning

| Knob | Default | When to change |
|---|---|---|
| `WithValidationRetry(.., maxRetries: N)` | `1` | Raise to 2 only if a measurable percentage of records exhaust retries *and* a third attempt actually recovers them (check `paperbase.{module}.extraction.attempts{success=false}` divided by total). Each retry is another 8–15s LLM call; raising blindly drags out the BackgroundJob queue. |
| `WithValidationRetry(.., serializerOptions: …)` | `JsonSerializerDefaults.Web` | Override only if your `T` uses non-Web casing conventions that MAF's built-in `RunAsync<T>` schema doesn't match. |
| Validator inheritance | — | Subclass `ContractExtractionValidator` (or your own) and override one `protected virtual Validate*` method to relax / tighten a single rule per deployment. |

## Tests

Three layers of tests cover the pattern:

- **Validator unit tests** — pure-function assertions on the rules and rule codes. See `modules/contracts/test/.../EventHandlers/ContractExtractionValidator_Tests.cs`.
- **Middleware unit tests** — Substitute<IChatClient> producing canned JSON sequences; assert call count, feedback injection, retry exhaustion. See `core/test/Dignite.Paperbase.Application.Tests/Agents/StructuredExtractionRetryMiddleware_Tests.cs`.
- **Domain guard** — exception assertions on `Contract.ApplyFields` with the existing aggregate test pattern.

End-to-end (BackgroundJob → real `IChatClient` → DB) is intentionally not unit-tested — those scenarios live in the host-level smoke tests because mocking LLM responses for full pipeline runs is more brittle than informative.
