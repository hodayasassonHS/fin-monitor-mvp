using FinMonitor.Core.Transactions;

namespace FinMonitor.Core.Tests.TestSupport;

/// <summary>
/// An <see cref="ITransactionEventBus"/> that records what was published without delivering it,
/// so a test can assert on the write path in isolation from the apply path.
/// </summary>
internal sealed class RecordingTransactionEventBus : ITransactionEventBus
{
    private readonly List<Transaction> _published = [];

    public IReadOnlyList<Transaction> Published => _published;

    public Task PublishAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        _published.Add(transaction);
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(TransactionEventHandler handler) => new NoOpSubscription();

    private sealed class NoOpSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
