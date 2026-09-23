namespace FinMonitor.Core.Transactions;

/// <summary>
/// Single-process <see cref="ITransactionEventBus"/>: the "cluster" is this one process.
/// </summary>
/// <remarks>
/// This is the default so that the service runs with <c>dotnet run</c> and no infrastructure at
/// all. Configure a Redis connection string and the Streams-backed bus takes over.
/// <para>
/// <b>Delivery is awaited, not fire-and-forget.</b> <see cref="PublishAsync"/> completes only
/// once every subscriber has applied the event, which gives an HTTP caller read-your-writes: a
/// <c>GET</c> issued after a <c>POST</c> returns is guaranteed to see it. The distributed bus
/// cannot offer that — it is eventually consistent by nature — and the README calls out the
/// difference rather than papering over it.
/// </para>
/// </remarks>
public sealed class InProcessTransactionEventBus : ITransactionEventBus
{
    private readonly TransactionEventHandlerRegistry _subscribers = new();

    public Task PublishAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return _subscribers.DispatchAsync(transaction, cancellationToken);
    }

    public IDisposable Subscribe(TransactionEventHandler handler) => _subscribers.Add(handler);
}
