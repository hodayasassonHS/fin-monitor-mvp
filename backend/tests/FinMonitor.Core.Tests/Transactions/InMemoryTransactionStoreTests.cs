using FinMonitor.Core.Tests.TestSupport;
using FinMonitor.Core.Transactions;
using Microsoft.Extensions.Options;

namespace FinMonitor.Core.Tests.Transactions;

public sealed class InMemoryTransactionStoreTests
{
    private static InMemoryTransactionStore StoreWithCapacity(int capacity) =>
        new(Options.Create(new TransactionStoreOptions { Capacity = capacity }));

    [Fact]
    public void Starts_empty()
    {
        var store = StoreWithCapacity(10);

        Assert.Equal(0, store.Count);
        Assert.Empty(store.GetLatest());
    }

    [Fact]
    public void Records_an_applied_transaction()
    {
        var store = StoreWithCapacity(10);
        var transaction = TransactionBuilder.A().Build();

        var result = store.Apply(transaction);

        Assert.True(result.Accepted);
        Assert.Equal(1, store.Count);
        Assert.Equal(transaction, store.Find(transaction.TransactionId));
    }

    [Fact]
    public void Returns_null_for_an_unknown_transaction()
    {
        var store = StoreWithCapacity(10);

        Assert.Null(store.Find(Guid.NewGuid()));
    }

    [Fact]
    public void Returns_transactions_most_recently_updated_first()
    {
        var store = StoreWithCapacity(10);
        var first = TransactionBuilder.A().Build();
        var second = TransactionBuilder.A().Build();

        store.Apply(first);
        store.Apply(second);

        Assert.Equal([second, first], store.GetLatest());
    }

    [Fact]
    public void Updates_in_place_rather_than_adding_a_second_row()
    {
        var store = StoreWithCapacity(10);
        var builder = TransactionBuilder.A();
        store.Apply(builder.Build());

        var completed = builder.WithStatus(TransactionStatus.Completed).SecondsLater(30).Build();
        store.Apply(completed);

        Assert.Equal(1, store.Count);
        Assert.Equal(TransactionStatus.Completed, store.Find(builder.TransactionId)!.Status);
    }

    [Fact]
    public void Moves_an_updated_transaction_back_to_the_front()
    {
        var store = StoreWithCapacity(10);
        var builder = TransactionBuilder.A();
        var other = TransactionBuilder.A().Build();

        store.Apply(builder.Build());
        store.Apply(other);
        var completed = builder.WithStatus(TransactionStatus.Completed).SecondsLater(30).Build();
        store.Apply(completed);

        Assert.Equal([completed, other], store.GetLatest());
    }

    [Fact]
    public void Leaves_state_untouched_when_the_merge_policy_rejects()
    {
        var store = StoreWithCapacity(10);
        var builder = TransactionBuilder.A().WithStatus(TransactionStatus.Completed);
        var completed = builder.Build();
        store.Apply(completed);

        var result = store.Apply(builder.WithStatus(TransactionStatus.Failed).SecondsLater(30).Build());

        Assert.False(result.Accepted);
        Assert.Equal(MergeRejectionReason.TerminalStatus, result.RejectionReason);
        Assert.Equal(completed, store.Find(builder.TransactionId));
    }

    [Fact]
    public void Evicts_the_least_recently_updated_transaction_when_full()
    {
        var store = StoreWithCapacity(2);
        var oldest = TransactionBuilder.A().Build();
        var middle = TransactionBuilder.A().Build();
        var newest = TransactionBuilder.A().Build();

        store.Apply(oldest);
        store.Apply(middle);
        store.Apply(newest);

        Assert.Equal(2, store.Count);
        Assert.Null(store.Find(oldest.TransactionId));
        Assert.Equal([newest, middle], store.GetLatest());
    }

    [Fact]
    public void Spares_a_transaction_from_eviction_when_it_is_updated()
    {
        // Eviction is by recency of update, not of creation: a long-running Pending transaction
        // that is still receiving updates must not fall out of the window.
        var store = StoreWithCapacity(2);
        var builder = TransactionBuilder.A();
        var filler = TransactionBuilder.A().Build();

        store.Apply(builder.Build());
        store.Apply(filler);
        store.Apply(builder.WithStatus(TransactionStatus.Completed).SecondsLater(30).Build());
        store.Apply(TransactionBuilder.A().Build());

        Assert.NotNull(store.Find(builder.TransactionId));
        Assert.Null(store.Find(filler.TransactionId));
    }

    [Fact]
    public void Caps_the_number_of_rows_returned()
    {
        var store = StoreWithCapacity(10);
        for (var i = 0; i < 5; i++)
        {
            store.Apply(TransactionBuilder.A().Build());
        }

        Assert.Equal(3, store.GetLatest(limit: 3).Count);
        Assert.Empty(store.GetLatest(limit: 0));
        Assert.Equal(5, store.GetLatest(limit: 99).Count);
    }

    [Fact]
    public void Rejects_a_negative_limit()
    {
        var store = StoreWithCapacity(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetLatest(limit: -1));
    }

    [Fact]
    public void Filters_by_status()
    {
        var store = StoreWithCapacity(10);
        var failed = TransactionBuilder.A().WithStatus(TransactionStatus.Failed).Build();
        store.Apply(TransactionBuilder.A().Build());
        store.Apply(failed);
        store.Apply(TransactionBuilder.A().WithStatus(TransactionStatus.Completed).Build());

        Assert.Equal([failed], store.GetLatest(status: TransactionStatus.Failed));
    }

    [Fact]
    public void Applies_the_limit_after_the_status_filter()
    {
        // A naive implementation takes the newest N and then filters, which would return nothing
        // here even though matching rows exist further down the window.
        var store = StoreWithCapacity(10);
        var failed = TransactionBuilder.A().WithStatus(TransactionStatus.Failed).Build();
        store.Apply(failed);
        store.Apply(TransactionBuilder.A().Build());
        store.Apply(TransactionBuilder.A().Build());

        Assert.Equal([failed], store.GetLatest(limit: 1, status: TransactionStatus.Failed));
    }

    [Fact]
    public void Returns_a_snapshot_that_later_writes_cannot_disturb()
    {
        // The dashboard holds a snapshot while ingestion carries on behind it. If GetLatest
        // handed back live internal state, this enumeration would throw or silently change.
        var store = StoreWithCapacity(10);
        store.Apply(TransactionBuilder.A().Build());

        var snapshot = store.GetLatest();
        store.Apply(TransactionBuilder.A().Build());

        Assert.Single(snapshot);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Rejects_a_capacity_below_one()
    {
        var options = Options.Create(new TransactionStoreOptions { Capacity = 0 });

        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryTransactionStore(options));
    }
}
