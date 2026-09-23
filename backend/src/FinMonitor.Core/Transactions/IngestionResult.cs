namespace FinMonitor.Core.Transactions;

public enum IngestionStatus
{
    /// <summary>Published to the cluster. Maps to HTTP 202.</summary>
    Accepted,

    /// <summary>Already recorded with exactly this state; nothing to do. Maps to HTTP 200.</summary>
    Duplicate,

    /// <summary>The payload is not a well-formed transaction. Maps to HTTP 400.</summary>
    Invalid,

    /// <summary>Well-formed, but it contradicts what is already recorded. Maps to HTTP 409.</summary>
    Conflict,
}

/// <summary>The outcome of an ingestion attempt, in domain terms rather than HTTP terms.</summary>
public sealed record IngestionResult
{
    private static readonly IReadOnlyList<ValidationError> NoErrors = [];

    private IngestionResult(
        IngestionStatus status,
        Transaction? transaction,
        IReadOnlyList<ValidationError> errors,
        MergeRejectionReason? rejectionReason)
    {
        Status = status;
        Transaction = transaction;
        Errors = errors;
        RejectionReason = rejectionReason;
    }

    public IngestionStatus Status { get; }

    /// <summary>
    /// The transaction as the system now understands it: the newly accepted value, or the
    /// already-recorded one when the attempt was a duplicate or a conflict. Null only when the
    /// payload failed validation and no transaction could be parsed at all.
    /// </summary>
    public Transaction? Transaction { get; }

    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>Set only for <see cref="IngestionStatus.Conflict"/> and <see cref="IngestionStatus.Duplicate"/>.</summary>
    public MergeRejectionReason? RejectionReason { get; }

    public static IngestionResult Accepted(Transaction transaction) =>
        new(IngestionStatus.Accepted, transaction, NoErrors, null);

    public static IngestionResult Duplicate(Transaction existing) =>
        new(IngestionStatus.Duplicate, existing, NoErrors, MergeRejectionReason.Duplicate);

    public static IngestionResult Invalid(IReadOnlyList<ValidationError> errors) =>
        new(IngestionStatus.Invalid, null, errors, null);

    public static IngestionResult Conflict(Transaction existing, MergeRejectionReason reason) =>
        new(IngestionStatus.Conflict, existing, NoErrors, reason);
}
