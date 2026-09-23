using Microsoft.Extensions.Logging;

namespace FinMonitor.Core.Transactions;

/// <summary>
/// The write path: validates an inbound payload and publishes it to the cluster.
/// </summary>
/// <remarks>
/// Note what this class does <i>not</i> do: it never writes to <see cref="ITransactionStore"/>.
/// The store is written in exactly one place — the subscriber on
/// <see cref="ITransactionEventBus"/> — so the local replica and every remote replica reach
/// their state by the same code path. See the ADR on multi-replica synchronisation.
/// </remarks>
public sealed class TransactionIngestionService(
    TransactionValidator validator,
    ITransactionStore store,
    ITransactionEventBus eventBus,
    ILogger<TransactionIngestionService> logger)
{
    public async Task<IngestionResult> IngestAsync(
        TransactionIngestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = validator.Validate(request);

        if (!validation.IsValid)
        {
            logger.LogDebug(
                "Rejected malformed transaction payload: {Errors}",
                string.Join("; ", validation.Errors.Select(e => $"{e.Field}: {e.Message}")));

            return IngestionResult.Invalid(validation.Errors);
        }

        var incoming = validation.Value!;

        // Speculative merge against this replica's view, purely to give the caller a fast, useful
        // answer. The authoritative merge happens when the subscriber applies the event. On a
        // single replica the two always agree; across replicas this check can miss a conflict
        // that another replica has already recorded, in which case the event is published and
        // then rejected on apply — the cluster still converges, the caller just saw a 202 for
        // something that turned out to be a no-op. Being occasionally optimistic is the right
        // trade here: the alternative is a synchronous cluster-wide read on every ingest.
        var existing = store.Find(incoming.TransactionId);
        var merge = TransactionMergePolicy.Merge(existing, incoming);

        if (!merge.Accepted)
        {
            var reason = merge.RejectionReason!.Value;

            if (reason == MergeRejectionReason.Duplicate)
            {
                // A retried POST is a success, not an error. Returning 409 here would push
                // callers towards treating a safe retry as a failure.
                logger.LogDebug("Transaction {TransactionId} re-sent unchanged; ignoring.", incoming.TransactionId);
                return IngestionResult.Duplicate(merge.Value);
            }

            logger.LogInformation(
                "Rejected transaction {TransactionId}: {Reason}.",
                incoming.TransactionId,
                reason);

            return IngestionResult.Conflict(merge.Value, reason);
        }

        await eventBus.PublishAsync(incoming, cancellationToken).ConfigureAwait(false);

        logger.LogDebug(
            "Published transaction {TransactionId} with status {Status}.",
            incoming.TransactionId,
            incoming.Status);

        return IngestionResult.Accepted(incoming);
    }
}
