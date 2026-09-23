namespace FinMonitor.Core.Transactions;

/// <summary>
/// Decides whether an incoming transaction supersedes the one already held for the same id.
/// </summary>
/// <remarks>
/// This is a pure function with no state and no I/O, which is what makes the distributed design
/// work. Every replica applies the same ordered log of transactions through this same policy, so
/// every replica lands on the same state. Two properties matter:
/// <list type="bullet">
/// <item>
/// <b>Idempotent.</b> Re-applying an event that has already been applied is rejected as
/// <see cref="MergeRejectionReason.Duplicate"/>. A replica can therefore replay the whole
/// retained log on startup to rebuild its view without producing spurious updates.
/// </item>
/// <item>
/// <b>Monotonic.</b> State only ever moves forward — never backwards in time
/// (<see cref="MergeRejectionReason.StaleTimestamp"/>) and never out of a terminal status
/// (<see cref="MergeRejectionReason.TerminalStatus"/>). Late or re-ordered delivery cannot
/// corrupt a replica's view.
/// </item>
/// </list>
/// Because the policy is pure, it is also used <i>speculatively</i> on the ingestion path to
/// reject a bad write with an HTTP 409 before it is ever published. See
/// <see cref="TransactionIngestionService"/>.
/// </remarks>
public static class TransactionMergePolicy
{
    /// <summary>
    /// Merges <paramref name="incoming"/> into <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The currently stored transaction, or <c>null</c> if this id is new.</param>
    /// <param name="incoming">The newly observed transaction. Must share the id of <paramref name="existing"/>.</param>
    public static MergeResult Merge(Transaction? existing, Transaction incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        // A transaction id never seen before is always accepted.
        if (existing is null)
        {
            return MergeResult.Accept(incoming);
        }

        if (existing.TransactionId != incoming.TransactionId)
        {
            throw new ArgumentException(
                $"Cannot merge transaction '{incoming.TransactionId}' into '{existing.TransactionId}'.",
                nameof(incoming));
        }

        // Amount and currency are economic facts about the transaction, not mutable state. A
        // payload that disagrees with what we already recorded is a bug or an attack, not an
        // update, so it is refused outright rather than silently overwriting a financial figure.
        if (existing.Amount != incoming.Amount ||
            !string.Equals(existing.Currency, incoming.Currency, StringComparison.Ordinal))
        {
            return MergeResult.Reject(existing, MergeRejectionReason.ImmutableFieldChanged);
        }

        // Last-write-wins on the observation timestamp. Anything older than what we hold would
        // move state backwards, so it loses regardless of which replica saw it first.
        if (incoming.Timestamp < existing.Timestamp)
        {
            return MergeResult.Reject(existing, MergeRejectionReason.StaleTimestamp);
        }

        // Checked before the terminal guard: replaying a retained log re-delivers terminal
        // events, and those must read as harmless duplicates rather than illegal transitions.
        if (existing.Status == incoming.Status)
        {
            return MergeResult.Reject(existing, MergeRejectionReason.Duplicate);
        }

        if (existing.Status.IsTerminal())
        {
            return MergeResult.Reject(existing, MergeRejectionReason.TerminalStatus);
        }

        return MergeResult.Accept(incoming);
    }
}
