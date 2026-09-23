using System.Collections.Concurrent;
using FinMonitor.Core.Tests.TestSupport;
using FinMonitor.Core.Transactions;

namespace FinMonitor.Core.Tests.Transactions;

public sealed class InProcessTransactionEventBusTests
{
    private readonly InProcessTransactionEventBus _bus = new();

    [Fact]
    public async Task Delivers_a_published_transaction_to_a_subscriber()
    {
        var received = new List<Transaction>();
        using var _ = _bus.Subscribe((transaction, _) =>
        {
            received.Add(transaction);
            return Task.CompletedTask;
        });

        var published = TransactionBuilder.A().Build();
        await _bus.PublishAsync(published);

        Assert.Equal([published], received);
    }

    [Fact]
    public async Task Delivers_to_every_subscriber()
    {
        var first = 0;
        var second = 0;
        using var _ = _bus.Subscribe((_, _) => { first++; return Task.CompletedTask; });
        using var __ = _bus.Subscribe((_, _) => { second++; return Task.CompletedTask; });

        await _bus.PublishAsync(TransactionBuilder.A().Build());

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Publishing_with_no_subscribers_is_harmless()
    {
        await _bus.PublishAsync(TransactionBuilder.A().Build());
    }

    [Fact]
    public async Task Stops_delivering_once_a_subscription_is_disposed()
    {
        var received = 0;
        var subscription = _bus.Subscribe((_, _) => { received++; return Task.CompletedTask; });

        await _bus.PublishAsync(TransactionBuilder.A().Build());
        subscription.Dispose();
        await _bus.PublishAsync(TransactionBuilder.A().Build());

        Assert.Equal(1, received);
    }

    [Fact]
    public async Task Disposing_one_of_two_identical_subscriptions_leaves_the_other_active()
    {
        // Removing by delegate identity would take out both registrations and silently stop a
        // component that never unsubscribed.
        var received = 0;
        TransactionEventHandler handler = (_, _) => { received++; return Task.CompletedTask; };

        var first = _bus.Subscribe(handler);
        using var second = _bus.Subscribe(handler);

        first.Dispose();
        await _bus.PublishAsync(TransactionBuilder.A().Build());

        Assert.Equal(1, received);
    }

    [Fact]
    public async Task Disposing_a_subscription_twice_does_not_remove_an_unrelated_one()
    {
        var kept = 0;
        var subscription = _bus.Subscribe((_, _) => Task.CompletedTask);
        using var _ = _bus.Subscribe((_, _) => { kept++; return Task.CompletedTask; });

        subscription.Dispose();
        subscription.Dispose();
        await _bus.PublishAsync(TransactionBuilder.A().Build());

        Assert.Equal(1, kept);
    }

    [Fact]
    public async Task Completes_only_after_every_subscriber_has_finished()
    {
        // This is what gives an HTTP caller read-your-writes: by the time the POST returns, the
        // store has already been updated by the subscriber.
        var applied = false;
        using var _ = _bus.Subscribe(async (_, token) =>
        {
            await Task.Delay(20, token);
            applied = true;
        });

        await _bus.PublishAsync(TransactionBuilder.A().Build());

        Assert.True(applied);
    }

    [Fact]
    public async Task Surfaces_a_subscriber_failure_to_the_publisher()
    {
        using var _ = _bus.Subscribe((_, _) => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _bus.PublishAsync(TransactionBuilder.A().Build()));
    }

    [Fact]
    public async Task Delivers_every_transaction_when_many_threads_publish_at_once()
    {
        var received = new ConcurrentBag<Guid>();
        using var _ = _bus.Subscribe((transaction, _) =>
        {
            received.Add(transaction.TransactionId);
            return Task.CompletedTask;
        });

        const int publishers = 16;
        const int perPublisher = 100;

        await Task.WhenAll(Enumerable.Range(0, publishers).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < perPublisher; i++)
            {
                await _bus.PublishAsync(TransactionBuilder.A().Build());
            }
        })));

        Assert.Equal(publishers * perPublisher, received.Count);
    }

    [Fact]
    public async Task Subscribing_while_publishing_does_not_disturb_delivery_in_flight()
    {
        // Subscriptions are copy-on-write, so a publish already under way keeps iterating the
        // array it started with instead of throwing on a mutated collection.
        var received = 0;
        using var _ = _bus.Subscribe((_, _) => { Interlocked.Increment(ref received); return Task.CompletedTask; });

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _bus.Subscribe((_, _) => Task.CompletedTask).Dispose();
            }
        });

        while (!stop.IsCancellationRequested)
        {
            await _bus.PublishAsync(TransactionBuilder.A().Build());
        }

        await churn;

        Assert.True(received > 0);
    }
}
