namespace FinMonitor.Core.Transactions;

/// <summary>Why <see cref="TransactionMergePolicy"/> declined to apply an incoming transaction.</summary>
public enum MergeRejectionReason
{
    /// <summary>The incoming value is indistinguishable from what is already stored.</summary>
    Duplicate,

    /// <summary>The incoming value is older than what is already stored, so it would move state backwards.</summary>
    StaleTimestamp,

    /// <summary>The stored transaction already reached a terminal status and cannot change again.</summary>
    TerminalStatus,

    /// <summary>The incoming value changes a field that is immutable after first observation.</summary>
    ImmutableFieldChanged,
}

/// <summary>
/// The outcome of merging an incoming transaction with the currently stored one.
/// </summary>
public readonly record struct MergeResult
{
    private MergeResult(bool accepted, Transaction value, MergeRejectionReason? reason)
    {
        Accepted = accepted;
        Value = value;
        RejectionReason = reason;
    }

    /// <summary>True when <see cref="Value"/> should replace the stored transaction.</summary>
    public bool Accepted { get; }

    /// <summary>The winning transaction: the incoming one when accepted, the stored one when rejected.</summary>
    public Transaction Value { get; }

    /// <summary>Set only when <see cref="Accepted"/> is false.</summary>
    public MergeRejectionReason? RejectionReason { get; }

    public static MergeResult Accept(Transaction incoming) => new(true, incoming, null);

    public static MergeResult Reject(Transaction existing, MergeRejectionReason reason) =>
        new(false, existing, reason);
}
