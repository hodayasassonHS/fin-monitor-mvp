using System.Globalization;

namespace FinMonitor.Core.Transactions;

/// <summary>
/// Parses an untrusted <see cref="TransactionIngestRequest"/> into a domain
/// <see cref="Transaction"/>, or explains why it cannot.
/// </summary>
/// <remarks>
/// Follows parse-don't-validate: the only way to obtain a <see cref="Transaction"/> from the
/// wire is through here, so any value reaching the rest of the system is already normalised
/// (currency upper-cased, timestamp in UTC). Nothing downstream re-checks these invariants.
/// </remarks>
public sealed class TransactionValidator
{
    /// <summary>
    /// How far ahead of the server clock a timestamp may be before it is rejected. Clients and
    /// servers drift by small amounts, so a little slack is normal; a wildly future-dated event
    /// would win last-write-wins forever and permanently freeze the transaction, so it is refused.
    /// </summary>
    internal static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Guards against overflow-style inputs; no legitimate MVP transaction approaches this.</summary>
    internal const decimal MaxAmount = 1_000_000_000m;

    private readonly TimeProvider _timeProvider;

    public TransactionValidator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public ValidationResult Validate(TransactionIngestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ValidationError>();

        var transactionId = ParseTransactionId(request.TransactionId, errors);
        var amount = ParseAmount(request.Amount, errors);
        var currency = ParseCurrency(request.Currency, errors);
        var status = ParseStatus(request.Status, errors);
        var timestamp = ParseTimestamp(request.Timestamp, errors);

        if (errors.Count > 0)
        {
            return ValidationResult.Failure(errors);
        }

        return ValidationResult.Success(new Transaction(
            transactionId!.Value,
            amount!.Value,
            currency!,
            status!.Value,
            timestamp!.Value));
    }

    private static Guid? ParseTransactionId(string? raw, List<ValidationError> errors)
    {
        const string field = "transactionId";

        if (string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new ValidationError(field, "A transaction id is required."));
            return null;
        }

        if (!Guid.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            errors.Add(new ValidationError(field, "Must be a GUID."));
            return null;
        }

        if (parsed == Guid.Empty)
        {
            errors.Add(new ValidationError(field, "Must not be the empty GUID."));
            return null;
        }

        return parsed;
    }

    private static decimal? ParseAmount(decimal? raw, List<ValidationError> errors)
    {
        const string field = "amount";

        if (raw is not { } amount)
        {
            errors.Add(new ValidationError(field, "An amount is required."));
            return null;
        }

        // Refunds and chargebacks are modelled as their own transactions in a real ledger rather
        // than as negative amounts, so the monitor treats a non-positive amount as bad input.
        if (amount <= 0m)
        {
            errors.Add(new ValidationError(field, "Must be greater than zero."));
            return null;
        }

        if (amount > MaxAmount)
        {
            errors.Add(new ValidationError(
                field,
                $"Must not exceed {MaxAmount.ToString("N0", CultureInfo.InvariantCulture)}."));
            return null;
        }

        return amount;
    }

    private static string? ParseCurrency(string? raw, List<ValidationError> errors)
    {
        const string field = "currency";

        if (string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new ValidationError(field, "A currency is required."));
            return null;
        }

        var trimmed = raw.Trim();

        if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetter))
        {
            errors.Add(new ValidationError(field, "Must be a three-letter ISO 4217 code, e.g. 'USD'."));
            return null;
        }

        return trimmed.ToUpperInvariant();
    }

    private static TransactionStatus? ParseStatus(string? raw, List<ValidationError> errors)
    {
        const string field = "status";

        if (string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new ValidationError(field, "A status is required."));
            return null;
        }

        var trimmed = raw.Trim();

        // Enum.TryParse also accepts the underlying ordinal, so "1" would quietly parse as
        // Completed. The wire contract is the status *name*; accepting an ordinal would couple
        // callers to this enum's declaration order and turn a reordering into a silent data
        // corruption. Requiring letters rules that out before parsing.
        if (!trimmed.All(char.IsAsciiLetter) ||
            !Enum.TryParse<TransactionStatus>(trimmed, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            errors.Add(new ValidationError(field, "Must be one of 'Pending', 'Completed' or 'Failed'."));
            return null;
        }

        return parsed;
    }

    private DateTimeOffset? ParseTimestamp(DateTimeOffset? raw, List<ValidationError> errors)
    {
        const string field = "timestamp";

        if (raw is not { } timestamp)
        {
            errors.Add(new ValidationError(field, "A timestamp is required."));
            return null;
        }

        var now = _timeProvider.GetUtcNow();

        if (timestamp > now + MaxClockSkew)
        {
            errors.Add(new ValidationError(
                field,
                $"Must not be more than {MaxClockSkew.TotalMinutes:N0} minutes in the future."));
            return null;
        }

        // Normalise to UTC so that two clients reporting the same instant in different offsets
        // compare equal, and so last-write-wins never depends on the caller's time zone.
        return timestamp.ToUniversalTime();
    }
}
