namespace FinMonitor.Redis;

public sealed class RedisTransactionBusOptions
{
    public const string SectionName = "Redis";

    /// <summary>
    /// StackExchange.Redis connection string. Leaving this empty keeps the service in
    /// single-replica mode with the in-process bus — which is what makes <c>dotnet run</c> work
    /// with no infrastructure at all.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>The Redis Stream every replica appends to and reads from.</summary>
    public string StreamKey { get; set; } = "finmonitor:transactions";

    /// <summary>
    /// Pub/sub channel used purely as a doorbell. It carries no transaction data, so the fact
    /// that pub/sub is at-most-once cannot lose anything: a dropped notification only costs
    /// latency until the next safety poll, and the data always comes from the stream.
    /// </summary>
    public string NotificationChannel { get; set; } = "finmonitor:transactions:wake";

    /// <summary>
    /// Entries retained in the stream. This is the replay window a restarting replica uses to
    /// rebuild its view, so it should be at least the store's capacity.
    /// </summary>
    public int StreamMaxLength { get; set; } = 500;

    /// <summary>Maximum entries read per round trip while draining.</summary>
    public int BatchSize { get; set; } = 256;

    /// <summary>
    /// How long to wait for a doorbell before reading the stream anyway. This is a backstop for
    /// a missed notification, not the normal path — steady-state latency is set by pub/sub.
    /// </summary>
    public TimeSpan SafetyPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ConnectionString);
}
