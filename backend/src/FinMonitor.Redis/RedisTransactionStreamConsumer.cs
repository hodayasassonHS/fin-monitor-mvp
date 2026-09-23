using FinMonitor.Core.Transactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FinMonitor.Redis;

/// <summary>
/// Follows the shared transaction stream and replays it into this replica.
/// </summary>
/// <remarks>
/// Runs on every replica, and every replica reads the whole stream — including the entries it
/// published itself. There are no consumer groups here on purpose: a consumer group hands each
/// entry to exactly one member, which is right for sharing work and exactly wrong for sharing
/// state. Each replica needs its own complete copy so that a dashboard client is served the same
/// data whichever replica it happens to reach.
/// <para>
/// Reading begins at the start of the retained stream, so a replica that has just started — a
/// restart, a rollout, a scale-up — rebuilds its window before it serves anyone. Replay is safe
/// because <see cref="TransactionMergePolicy"/> is idempotent: re-applying an entry that is
/// already present is a no-op.
/// </para>
/// </remarks>
internal sealed partial class RedisTransactionStreamConsumer(
    IConnectionMultiplexer connection,
    RedisStreamTransactionEventBus bus,
    IOptions<RedisTransactionBusOptions> options,
    ILogger<RedisTransactionStreamConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly RedisTransactionBusOptions _options = options.Value;

    /// <summary>
    /// Signals "there may be new entries". Capacity of one because the signal is level, not
    /// edge: ten notifications while the consumer is busy still mean one drain.
    /// </summary>
    private readonly SemaphoreSlim _doorbell = new(0, 1);

    /// <summary>Id of the last entry handed to subscribers; where the next read resumes from.</summary>
    private RedisValue _position = StreamPosition.Beginning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = RedisChannel.Literal(_options.NotificationChannel);
        var subscriber = connection.GetSubscriber();

        await subscriber.SubscribeAsync(channel, (_, _) => RingDoorbell()).ConfigureAwait(false);

        LogFollowing(_options.StreamKey);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DrainAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (RedisException ex)
                {
                    // Redis is unreachable or failing over. The position is retained, so once the
                    // multiplexer reconnects the drain resumes exactly where it left off.
                    LogDrainFailed(ex);
                    await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Returns as soon as a replica publishes; the timeout is only a backstop against
                // a notification that was dropped while we were disconnected.
                await _doorbell.WaitAsync(_options.SafetyPollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _doorbell.Dispose();
        base.Dispose();
    }

    /// <summary>Reads forward from <see cref="_position"/> until the stream has nothing more.</summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var database = connection.GetDatabase();

        while (!cancellationToken.IsCancellationRequested)
        {
            var entries = await database
                .StreamReadAsync(_options.StreamKey, _position, _options.BatchSize)
                .ConfigureAwait(false);

            if (entries.Length == 0)
            {
                return;
            }

            foreach (var entry in entries)
            {
                await ApplyAsync(entry, cancellationToken).ConfigureAwait(false);

                // Advanced even when the entry could not be parsed or a handler threw: the
                // alternative is re-reading the same bad entry for ever and never seeing a live
                // one again. Losing one event is recoverable; a stuck consumer is not.
                _position = entry.Id;
            }
        }
    }

    private async Task ApplyAsync(StreamEntry entry, CancellationToken cancellationToken)
    {
        if (!TransactionStreamEntry.TryParse(entry, out var transaction))
        {
            LogUnreadableEntry(entry.Id.ToString());
            return;
        }

        try
        {
            await bus.Subscribers.DispatchAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure to push to one client must not stop this replica consuming the stream.
            LogHandlerFailed(ex, transaction.TransactionId);
        }
    }

    private void RingDoorbell()
    {
        // The check is an optimisation, not a guarantee; the catch covers the race where two
        // notifications arrive at once and both see an empty semaphore.
        if (_doorbell.CurrentCount > 0)
        {
            return;
        }

        try
        {
            _doorbell.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled — nothing to do.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Following transaction stream '{StreamKey}' from the start of its retained window.")]
    private partial void LogFollowing(string streamKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read the transaction stream; retrying.")]
    private partial void LogDrainFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped unreadable stream entry '{EntryId}'.")]
    private partial void LogUnreadableEntry(string entryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A subscriber failed while handling transaction {TransactionId}.")]
    private partial void LogHandlerFailed(Exception exception, Guid transactionId);
}
