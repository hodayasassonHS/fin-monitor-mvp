using System.Collections.Concurrent;
using FinMonitor.Core.Tests.TestSupport;
using FinMonitor.Core.Transactions;
using Microsoft.Extensions.Options;

namespace FinMonitor.Core.Tests.Transactions;

/// <summary>
/// Concurrency tests for the store.
/// </summary>
/// <remarks>
/// Races are probabilistic, so these tests are written to maximise the chance of exposing one:
/// every worker waits on a <see cref="Barrier"/> so that they all start inside the contended
/// region at the same moment, and the work is sized to run for long enough that the scheduler
/// interleaves them. A broken implementation does not fail these every time, but it fails them
/// often enough to be caught — which is the most any concurrency test can offer.
/// </remarks>
public sealed class InMemoryTransactionStoreConcurrencyTests
{
    private static readonly int Workers = Math.Max(8, Environment.ProcessorCount * 2);

    private static InMemoryTransactionStore StoreWithCapacity(int capacity) =>
        new(Options.Create(new TransactionStoreOptions { Capacity = capacity }));

    [Fact]
    public void Records_every_transaction_when_many_threads_write_at_once()
    {
        const int perWorker = 250;
        var store = StoreWithCapacity(Workers * perWorker);
        var written = new ConcurrentBag<Guid>();

        RunInParallel(_ =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                var transaction = TransactionBuilder.A().Build();
                Assert.True(store.Apply(transaction).Accepted);
                written.Add(transaction.TransactionId);
            }
        });

        // A lost update would show up as a count below the number written; a duplicated insert
        // would show up as one above it.
        Assert.Equal(Workers * perWorker, store.Count);
        Assert.All(written, id => Assert.NotNull(store.Find(id)));
    }

    [Fact]
    public void Lets_exactly_one_writer_win_when_all_threads_apply_the_same_transaction()
    {
        // The read-merge-write in Apply has to be one indivisible step. If two threads could both
        // read "not present" before either wrote, both would be accepted and the transaction
        // would be inserted twice.
        var store = StoreWithCapacity(100);
        var transaction = TransactionBuilder.A().Build();
        var accepted = 0;

        RunInParallel(_ =>
        {
            if (store.Apply(transaction).Accepted)
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.Equal(1, accepted);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Settles_on_the_same_final_state_whichever_thread_wins()
    {
        // Last-write-wins means the outcome depends on the timestamps, not on who got there
        // first. Repeated so that both interleavings are hit.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var store = StoreWithCapacity(10);
            var builder = TransactionBuilder.A();
            var pending = builder.Build();
            var completed = builder.WithStatus(TransactionStatus.Completed).SecondsLater(30).Build();

            RunInParallel(worker => store.Apply(worker % 2 == 0 ? pending : completed));

            Assert.Equal(completed, store.Find(builder.TransactionId));
        }
    }

    [Fact]
    public void Never_exceeds_its_capacity_under_concurrent_load()
    {
        const int capacity = 64;
        var store = StoreWithCapacity(capacity);

        RunInParallel(_ =>
        {
            for (var i = 0; i < 500; i++)
            {
                store.Apply(TransactionBuilder.A().Build());
                Assert.True(store.Count <= capacity, $"Store held {store.Count} rows, over capacity {capacity}.");
            }
        });

        Assert.Equal(capacity, store.Count);
    }

    [Fact]
    public async Task Serves_consistent_snapshots_while_writes_are_in_flight()
    {
        // Readers must never see the index and the recency list disagree, and must never catch
        // the collection mid-mutation. Without the lock this is where the implementation throws.
        const int capacity = 128;
        var store = StoreWithCapacity(capacity);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var reads = 0;

            while (!stop.IsCancellationRequested)
            {
                var snapshot = store.GetLatest();

                Assert.True(snapshot.Count <= capacity);

                // The index and the recency list disagreeing would surface as the same id
                // appearing twice in one snapshot.
                Assert.Equal(snapshot.Select(t => t.TransactionId).Distinct().Count(), snapshot.Count);

                reads++;
            }

            return reads;
        })).ToArray();

        RunInParallel(_ =>
        {
            while (!stop.IsCancellationRequested)
            {
                store.Apply(TransactionBuilder.A().Build());
            }
        });

        var totalReads = await Task.WhenAll(readers);

        Assert.All(totalReads, reads => Assert.True(reads > 0, "A reader never completed a snapshot."));
    }

    /// <summary>
    /// Runs <paramref name="work"/> on <see cref="Workers"/> threads released simultaneously,
    /// and rethrows the first failure rather than letting it vanish on a background thread.
    /// </summary>
    private static void RunInParallel(Action<int> work)
    {
        using var barrier = new Barrier(Workers);

        var tasks = Enumerable.Range(0, Workers)
            .Select(worker => Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    work(worker);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        Task.WaitAll(tasks);
    }
}
