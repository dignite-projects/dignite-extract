namespace Dignite.Paperbase.Abstractions.Agents;

/// <summary>
/// Validates a single structured extraction result of type <typeparamref name="T"/>.
/// Implementations live in the business module that owns the extraction shape (e.g.
/// <c>ContractExtractionValidator</c> in the contracts module) and are registered as
/// <c>ITransientDependency</c>; the retry middleware picks them up via DI.
/// </summary>
public interface IExtractionValidator<T>
{
    /// <summary>
    /// Inspect <paramref name="result"/> and report whether it satisfies the domain's
    /// invariants. Pure function — no side effects, no I/O, no logging.
    /// </summary>
    ExtractionValidationResult Validate(T result);
}
