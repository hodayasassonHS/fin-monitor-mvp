namespace FinMonitor.Core.Transactions;

/// <summary>A single rejected field and the reason it was rejected.</summary>
public sealed record ValidationError(string Field, string Message);

/// <summary>
/// The result of turning an untrusted <see cref="TransactionIngestRequest"/> into a
/// <see cref="Transaction"/>: either the parsed value, or the reasons it could not be parsed.
/// </summary>
public sealed class ValidationResult
{
    private static readonly IReadOnlyList<ValidationError> NoErrors = [];

    private ValidationResult(Transaction? value, IReadOnlyList<ValidationError> errors)
    {
        Value = value;
        Errors = errors;
    }

    /// <summary>The parsed, normalised transaction. Non-null exactly when <see cref="IsValid"/>.</summary>
    public Transaction? Value { get; }

    public IReadOnlyList<ValidationError> Errors { get; }

    public bool IsValid => Value is not null;

    public static ValidationResult Success(Transaction value) => new(value, NoErrors);

    public static ValidationResult Failure(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException("A failed validation must report at least one error.", nameof(errors));
        }

        return new ValidationResult(null, errors);
    }
}
