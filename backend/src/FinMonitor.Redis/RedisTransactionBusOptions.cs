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
    /// Entries retained in the stream. This is the replay window a restarting replica uses to
    /// rebuild its view, so it should be at least the store's capacity.
    /// </summary>
    public int StreamMaxLength { get; set; } = 500;

    /// <summary>Maximum entries read per round trip while draining.</summary>
    public int BatchSize { get; set; } = 256;

    /// <summary>How often the consumer checks the stream for new entries.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ConnectionString);
}