using FinMonitor.Core.Transactions;
using Microsoft.Extensions.Logging;
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
/// <b>Why pub/sub is here as well.</b> StackExchange.Redis does not expose blocking <c>XREAD</c>
/// — a blocking command would occupy the shared multiplexer — so following a stream means
/// polling, and polling means trading latency against load. Publishing a contentless doorbell on
/// a pub/sub channel removes that trade: the consumer sleeps until it is woken, then drains the
/// stream. Because the notification carries no data, losing one costs only latency until the
/// next safety poll; correctness still rests entirely on the stream.
/// </para>
/// <para>
/// See <c>docs/adr/0003-multi-replica-synchronisation.md</c> for the alternatives considered.
/// </para>
/// </remarks>
public sealed class RedisStreamTransactionEventBus(
    IConnectionMultiplexer connection,
    IOptions<RedisTransactionBusOptions> options,
    ILogger<RedisStreamTransactionEventBus> logger) : ITransactionEventBus
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
            useApproximateMaxLength: true).ConfigureAwait(false);

        // Best effort. The entry is already durably in the stream, so a failed doorbell delays
        // delivery to the next safety poll rather than losing the transaction.
        try
        {
            await connection.GetSubscriber()
                .PublishAsync(RedisChannel.Literal(_options.NotificationChannel), RedisValue.EmptyString)
                .ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(
                ex,
                "Could not notify replicas about transaction {TransactionId}; it will be picked up by the next poll.",
                transaction.TransactionId);
        }
    }

    /// <remarks>
    /// Registers a local handler only. Events reach it by way of
    /// <see cref="RedisTransactionStreamConsumer"/> reading the shared stream — including events
    /// this very replica published, which is what keeps the local and remote paths identical.
    /// </remarks>
    public IDisposable Subscribe(TransactionEventHandler handler) => Subscribers.Add(handler);
}
