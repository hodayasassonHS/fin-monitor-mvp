namespace FinMonitor.Core.Transactions;

/// <summary>
/// A single financial transaction as tracked by the monitor.
/// </summary>
/// <remarks>
/// Immutable by design. Every replica of the system holds copies of this value and applies
/// events concurrently; an immutable record means a snapshot handed to a reader can never be
/// mutated underneath it, which removes a whole class of locking from the read path.
/// <para>
/// <see cref="Amount"/> is <see cref="decimal"/> rather than <c>double</c>: binary floating
/// point cannot represent most decimal fractions exactly and must never be used for money.
/// </para>
/// </remarks>
/// <param name="TransactionId">Client-supplied identity, also used as the idempotency key.</param>
/// <param name="Amount">Positive monetary amount, in <paramref name="Currency"/> units.</param>
/// <param name="Currency">Upper-case ISO 4217 alphabetic code, e.g. <c>USD</c>.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Timestamp">Instant the state was observed, always normalised to UTC.</param>
public sealed record Transaction(
    Guid TransactionId,
    decimal Amount,
    string Currency,
    TransactionStatus Status,
    DateTimeOffset Timestamp);
