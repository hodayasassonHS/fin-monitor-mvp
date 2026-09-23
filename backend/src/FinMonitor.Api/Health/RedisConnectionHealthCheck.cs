using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace FinMonitor.Api.Health;

/// <summary>
/// Readiness probe for the distributed event bus.
/// </summary>
/// <remarks>
/// Only meaningful when the service is running in multi-replica mode; with no Redis configured
/// there is no multiplexer in the container and the check reports healthy.
/// <para>
/// Reported as <b>degraded</b> rather than unhealthy while disconnected. A replica that cannot
/// reach Redis can still serve its own snapshot and its own ingestion — it is stale, not broken
/// — and failing readiness would pull every replica out of the load balancer during a Redis
/// blip, turning a partial outage into a total one.
/// </para>
/// </remarks>
internal sealed class RedisConnectionHealthCheck(IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (services.GetService(typeof(IConnectionMultiplexer)) is not IConnectionMultiplexer connection)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Running single-replica; no event bus to check."));
        }

        return Task.FromResult(connection.IsConnected
            ? HealthCheckResult.Healthy("Connected to the transaction stream.")
            : HealthCheckResult.Degraded("Disconnected from the transaction stream; serving a stale view."));
    }
}
