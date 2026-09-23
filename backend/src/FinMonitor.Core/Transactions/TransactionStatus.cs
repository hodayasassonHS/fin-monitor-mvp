namespace FinMonitor.Core.Transactions;

/// <summary>
/// Lifecycle state of a transaction. <see cref="Completed"/> and <see cref="Failed"/> are
/// terminal: once observed, a transaction never leaves them.
/// </summary>
public enum TransactionStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
}

public static class TransactionStatusExtensions
{
    /// <summary>A terminal status is the end of the lifecycle and cannot be transitioned away from.</summary>
    public static bool IsTerminal(this TransactionStatus status) =>
        status is TransactionStatus.Completed or TransactionStatus.Failed;
}
