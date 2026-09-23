using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FinMonitor.Api.Tests.TestSupport;

/// <summary>
/// Hosts the real application in-process for integration tests.
/// </summary>
/// <remarks>
/// Nothing is stubbed out. These tests exercise the same endpoint routing, JSON contract,
/// dependency graph and hub pipeline that runs in production — the only difference is that no
/// Redis connection string is configured, so the in-process bus stands in for the cluster. That
/// substitution is exactly the one the design is built around, and the code path from ingestion
/// through the bus to the store and out to connected clients is identical either way.
/// </remarks>
internal sealed class FinMonitorApiFactory : WebApplicationFactory<Program>
{
    public int Capacity { get; init; } = 200;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TransactionStore:Capacity"] = Capacity.ToString(),

                // Explicitly single-replica: these tests assert on read-your-writes, which only
                // the in-process bus guarantees.
                ["Redis:ConnectionString"] = string.Empty,
            }));

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }
}
