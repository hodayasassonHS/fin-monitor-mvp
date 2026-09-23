namespace FinMonitor.Core.Transactions;

/// <summary>Handles a transaction observed anywhere in the cluster.</summary>
public delegate Task TransactionEventHandler(Transaction transaction, CancellationToken cancellationToken);

/// <summary>
/// The fan-out seam between ingestion and everything that reacts to a transaction.
/// </summary>
/// <remarks>
/// This is the single extension point that makes the service horizontally scalable. Ingestion
/// never writes to the store or touches SignalR directly; it publishes here, and a subscriber
/// running on <i>every</i> replica applies the event locally. Swapping the implementation from
/// <see cref="InProcessTransactionEventBus"/> to the Redis Streams one turns a single-process
/// app into a multi-replica one without a line changing anywhere else.
/// </remarks>
public interface ITransactionEventBus
{
    /// <summary>
    /// Publishes a transaction to every replica, including the one that called this method.
    /// </summary>
    /// <remarks>
    /// The publisher is <i>not</i> special-cased into applying the event itself. Routing every
    /// replica through the identical subscribe-and-apply path means the local case and the
    /// remote case cannot drift apart, and the distributed path is exercised by every test that
    /// posts a transaction.
    /// </remarks>
    Task PublishAsync(Transaction transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a handler for transactions published anywhere in the cluster.
    /// Disposing the returned token unsubscribes.
    /// </summary>
    IDisposable Subscribe(TransactionEventHandler handler);
}
