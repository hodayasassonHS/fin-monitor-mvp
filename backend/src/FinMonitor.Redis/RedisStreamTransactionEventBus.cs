using FinMonitor.Core.Transactions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FinMonitor.Redis;

/// <summary>
/// The distributed <see cref="ITransactionEventBus"/>: a Redis Stream is the shared log every
/// replica appends to and replays from.
/// </summary>
/// <remarks>
/// <b>Why a stream rather than plain pub/sub.</b> Pub/sub is at-most-once and has no history, so
/// a replica that restarts — or one that joins during a scale-up — would silently hold a thinner
/// view of the world than its peers, for ever. A stream is a durable, capped log: a starting
/// replica replays it from the beginning to rebuild its window, then follows the tail. The
/// retention bound (<c>MAXLEN</c>) is deliberately the same shape as the store's capacity, so
/// "what a replica can recover" and "what a replica keeps" are the same window by construction.
/// <para>
/// See <c>docs/adr/0003-multi-replica-synchronisation.md</c> for the alternatives considered.
/// </para>
/// </remarks>
public sealed class RedisStreamTransactionEventBus(
    IConnectionMultiplexer connection,
    IOptions<RedisTransactionBusOptions> options) : ITransactionEventBus
{
    private readonly RedisTransactionBusOptions _options = options.Value;

    internal TransactionEventHandlerRegistry Subscribers { get; } = new();

    public async Task PublishAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        var database = connection.GetDatabase();

        // Approximate trimming ("~") lets Redis trim on node boundaries instead of walking the
        // stream on every append. The window then sits a little above MaxLength, which is a good
        // trade for an O(1) write.
        await database.StreamAddAsync(
            _options.StreamKey,
            TransactionStreamEntry.ToEntries(transaction),
            messageId: null,
            maxLength: _options.StreamMaxLength,
            useApproximateMaxLength: true);
    }

    /// <remarks>
    /// Registers a local handler only. Events reach it by way of
    /// <see cref="RedisTransactionStreamConsumer"/> reading the shared stream — including events
    /// this very replica published, which is what keeps the local and remote paths identical.
    /// </remarks>
    public IDisposable Subscribe(TransactionEventHandler handler) => Subscribers.Add(handler);
}