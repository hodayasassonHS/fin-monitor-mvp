using FinMonitor.Api.Contracts;
using FinMonitor.Core.Transactions;
using Microsoft.AspNetCore.SignalR;

namespace FinMonitor.Api.Realtime;

/// <summary>
/// The one and only writer to <see cref="ITransactionStore"/>: applies every transaction the bus
/// delivers, then pushes accepted changes to the dashboards connected to this replica.
/// </summary>
/// <remarks>
/// This is where the whole design converges. Transactions arrive here whether they were ingested
/// by this replica or by another one, because ingestion publishes to the bus rather than writing
/// directly, so there is a single path from "a transaction happened" to "the store reflects it
/// and clients know". A second writer would be a second opportunity for replicas to disagree.
/// <para>
/// <b>Thread safety across many connections.</b> Broadcasts go through
/// <see cref="IHubContext{THub,T}"/>, whose lifetime manager is designed to be invoked
/// concurrently and handles per-connection write serialisation internally. The store's own lock
/// covers the apply. There is no shared mutable state in this class at all, which is why it can
/// serve any number of simultaneous connections without additional synchronisation.
/// </para>
/// </remarks>
internal sealed partial class TransactionProjectionService(
    ITransactionEventBus eventBus,
    ITransactionStore store,
    IHubContext<TransactionsHub, ITransactionsClient> hub,
    ILogger<TransactionProjectionService> logger) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = eventBus.Subscribe(ProjectAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private async Task ProjectAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        var result = store.Apply(transaction);

        if (!result.Accepted)
        {
            // Expected and frequent: replaying the retained log on startup re-delivers
            // everything this replica already has. Suppressing the broadcast here is what stops
            // a restart from flooding every connected dashboard with stale updates.
            LogNotApplied(transaction.TransactionId, result.RejectionReason!.Value);
            return;
        }

        // Deliberately after the store write. A client that receives an update and immediately
        // asks this replica for a snapshot must not be told the transaction does not exist.
        await hub.Clients.All
            .TransactionUpserted(TransactionDto.From(transaction))
            .ConfigureAwait(false);

        LogBroadcast(transaction.TransactionId, transaction.Status);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Broadcast transaction {TransactionId} as {Status}.")]
    private partial void LogBroadcast(Guid transactionId, TransactionStatus status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Did not apply transaction {TransactionId}: {Reason}.")]
    private partial void LogNotApplied(Guid transactionId, MergeRejectionReason reason);
}
