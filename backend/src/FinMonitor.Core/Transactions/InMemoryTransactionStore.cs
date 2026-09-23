using Microsoft.Extensions.Options;

namespace FinMonitor.Core.Transactions;

/// <summary>
/// A bounded, thread-safe, in-memory window over the most recently updated transactions.
/// </summary>
/// <remarks>
/// <b>Why a single lock rather than <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>.</b>
/// An <see cref="Apply"/> has to do four things as one indivisible step: read the current value,
/// run the merge policy against it, move the entry to the front of the recency order, and evict
/// the tail if the window overflowed. <c>ConcurrentDictionary</c> gives atomicity per key, which
/// is not the invariant that needs protecting here — the recency list and the map have to agree
/// with each other. Trying to compose lock-free operations across two structures would need a
/// retry loop that is strictly more complex and no faster than simply taking the lock.
/// <para>
/// The lock is held only for pointer manipulation over an in-memory structure — no I/O, no
/// callbacks, no allocation beyond a list node — so contention stays negligible even with many
/// concurrent ingesting requests.
/// </para>
/// <para>
/// <b>Why reads are safe.</b> <see cref="GetLatest"/> copies into a fresh array while holding the
/// lock and returns that. Combined with <see cref="Transaction"/> being immutable, a caller can
/// hold and enumerate a snapshot indefinitely while writers carry on; there is no torn read and
/// no <c>InvalidOperationException</c> from a collection mutating mid-enumeration.
/// </para>
/// </remarks>
public sealed class InMemoryTransactionStore : ITransactionStore
{
    private readonly Lock _gate = new();

    /// <summary>Most recently updated first. Eviction takes from the tail.</summary>
    private readonly LinkedList<Transaction> _recency = new();

    private readonly Dictionary<Guid, LinkedListNode<Transaction>> _index;

    private readonly int _capacity;

    public InMemoryTransactionStore(IOptions<TransactionStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _capacity = options.Value.Capacity;
        ArgumentOutOfRangeException.ThrowIfLessThan(_capacity, 1);

        _index = new Dictionary<Guid, LinkedListNode<Transaction>>(_capacity);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    public MergeResult Apply(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        lock (_gate)
        {
            var existingNode = _index.GetValueOrDefault(transaction.TransactionId);
            var result = TransactionMergePolicy.Merge(existingNode?.Value, transaction);

            if (!result.Accepted)
            {
                return result;
            }

            if (existingNode is not null)
            {
                // A LinkedList node cannot have its value replaced in place, so the update is a
                // remove plus an insert at the front. Both are O(1).
                _recency.Remove(existingNode);
            }

            _index[transaction.TransactionId] = _recency.AddFirst(transaction);

            Evict();

            return result;
        }
    }

    public Transaction? Find(Guid transactionId)
    {
        lock (_gate)
        {
            return _index.GetValueOrDefault(transactionId)?.Value;
        }
    }

    public IReadOnlyList<Transaction> GetLatest(int? limit = null, TransactionStatus? status = null)
    {
        if (limit is { } requested)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(requested, nameof(limit));

            if (requested == 0)
            {
                return [];
            }
        }

        lock (_gate)
        {
            var capacity = Math.Min(limit ?? _index.Count, _index.Count);
            var results = new List<Transaction>(capacity);

            for (var node = _recency.First; node is not null; node = node.Next)
            {
                if (status is { } wanted && node.Value.Status != wanted)
                {
                    continue;
                }

                results.Add(node.Value);

                if (results.Count == limit)
                {
                    break;
                }
            }

            return results;
        }
    }

    /// <summary>Drops least recently updated entries until the window fits. Caller holds the lock.</summary>
    private void Evict()
    {
        while (_index.Count > _capacity)
        {
            var oldest = _recency.Last!;
            _recency.RemoveLast();
            _index.Remove(oldest.Value.TransactionId);
        }
    }
}
