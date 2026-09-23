namespace FinMonitor.Core.Transactions;

/// <summary>
/// Holds the most recent transactions the monitor knows about.
/// </summary>
/// <remarks>
/// Deliberately narrow. The dashboard needs "the latest N, newest first, optionally filtered by
/// status" and nothing else, so the abstraction does not pretend to be a repository. Swapping the
/// in-memory implementation for SQLite, Redis or Postgres means implementing these three members.
/// <para>All members must be safe to call concurrently from any number of threads.</para>
/// </remarks>
public interface ITransactionStore
{
    /// <summary>
    /// Atomically merges <paramref name="transaction"/> into the store using
    /// <see cref="TransactionMergePolicy"/>, and reports what happened.
    /// </summary>
    MergeResult Apply(Transaction transaction);

    /// <summary>Returns the currently stored transaction for <paramref name="transactionId"/>, if any.</summary>
    Transaction? Find(Guid transactionId);

    /// <summary>
    /// Returns an immutable point-in-time snapshot, most recently updated first.
    /// </summary>
    /// <param name="limit">Maximum number of rows to return. <c>null</c> means no limit.</param>
    /// <param name="status">When set, only transactions in this status are returned.</param>
    IReadOnlyList<Transaction> GetLatest(int? limit = null, TransactionStatus? status = null);

    /// <summary>Number of transactions currently retained.</summary>
    int Count { get; }
}
