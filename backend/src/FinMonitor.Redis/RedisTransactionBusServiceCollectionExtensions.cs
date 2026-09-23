using FinMonitor.Core.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FinMonitor.Redis;

public static class RedisTransactionBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis Streams event bus, but only when a connection string is configured.
    /// </summary>
    /// <remarks>
    /// Without one this is a no-op and the service keeps the in-process bus, so the same build
    /// runs as a single self-contained process locally and as a horizontally scaled deployment
    /// in a cluster. Nothing but configuration changes between the two.
    /// <para>
    /// Must be called <i>before</i> <c>AddTransactionCore</c>, which registers the in-process bus
    /// with <c>TryAdd</c> and therefore defers to whatever is already present.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRedisTransactionBus(
        this IServiceCollection services,
        Action<RedisTransactionBusOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RedisTransactionBusOptions();
        configure(options);

        services.Configure(configure);

        if (!options.IsEnabled)
        {
            return services;
        }

        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var configuration = ConfigurationOptions.Parse(options.ConnectionString!);

            // Do not fail startup when Redis is not up yet. In a cluster the two roll out
            // independently, and a pod that crash-loops waiting for its dependency is strictly
            // worse than one that starts, reports itself not-ready, and connects when it can.
            configuration.AbortOnConnectFail = false;
            configuration.ClientName = $"finmonitor-{Environment.MachineName}";

            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ConnectionMultiplexer));
            var connection = ConnectionMultiplexer.Connect(configuration);

            connection.ConnectionFailed += (_, args) =>
                logger.LogWarning(args.Exception, "Redis connection failed ({FailureType}).", args.FailureType);

            connection.ConnectionRestored += (_, _) =>
                logger.LogInformation("Redis connection restored.");

            return connection;
        });

        services.AddSingleton<RedisStreamTransactionEventBus>();
        services.AddSingleton<ITransactionEventBus>(
            provider => provider.GetRequiredService<RedisStreamTransactionEventBus>());

        services.AddHostedService<RedisTransactionStreamConsumer>();

        return services;
    }
}
