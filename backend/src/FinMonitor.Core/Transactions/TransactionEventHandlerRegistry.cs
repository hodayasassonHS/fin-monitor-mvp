namespace FinMonitor.Core.Transactions;

/// <summary>
/// The local subscriber list shared by every <see cref="ITransactionEventBus"/> implementation.
/// </summary>
/// <remarks>
/// Where an event comes from — this process or another replica — changes nothing about how it is
/// delivered locally, so that half lives here and each transport only has to solve its own
/// problem. It also means the in-process bus and the distributed one cannot drift apart in
/// subscription or ordering semantics.
/// <para>
/// Handlers are held in an immutable array swapped under a lock (copy-on-write). Dispatch reads
/// the reference once and iterates it without synchronisation, so the hot path never blocks, and
/// a subscription changing mid-dispatch cannot disturb a delivery already under way.
/// </para>
/// </remarks>
public sealed class TransactionEventHandlerRegistry
{
    private readonly Lock _gate = new();

    private volatile TransactionEventHandler[] _handlers = [];

    public IDisposable Add(TransactionEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }

        return new Subscription(this, handler);
    }

    /// <summary>Delivers <paramref name="transaction"/> to every current handler, in order.</summary>
    public async Task DispatchAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // Sequential rather than concurrent: handlers apply the event to the store and then push
        // it to connected clients, and a defined order is what guarantees a client never receives
        // a transaction the local store has not recorded yet.
        foreach (var handler in _handlers)
        {
            await handler(transaction, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Remove(TransactionEventHandler handler)
    {
        lock (_gate)
        {
            var remaining = new List<TransactionEventHandler>(_handlers.Length);
            var removed = false;

            foreach (var existing in _handlers)
            {
                // Remove exactly one occurrence: the same handler may legitimately be subscribed
                // twice, and disposing one token must not cancel the other.
                if (!removed && existing == handler)
                {
                    removed = true;
                    continue;
                }

                remaining.Add(existing);
            }

            _handlers = [.. remaining];
        }
    }

    private sealed class Subscription(TransactionEventHandlerRegistry registry, TransactionEventHandler handler)
        : IDisposable
    {
        private TransactionEventHandler? _handler = handler;

        public void Dispose()
        {
            // Interlocked so that a double dispose cannot remove a second, unrelated registration.
            var target = Interlocked.Exchange(ref _handler, null);

            if (target is not null)
            {
                registry.Remove(target);
            }
        }
    }
}
