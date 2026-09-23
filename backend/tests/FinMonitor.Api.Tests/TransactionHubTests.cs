using FinMonitor.Api.Contracts;
using FinMonitor.Api.Tests.TestSupport;

namespace FinMonitor.Api.Tests;

/// <summary>
/// End-to-end tests of the real-time path: HTTP ingestion in one end, a live SignalR client at
/// the other, with the store, the event bus and the hub all in between.
/// </summary>
public sealed class TransactionHubTests : IAsyncLifetime
{
    private readonly FinMonitorApiFactory _factory = new();

    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Sends_the_current_state_to_a_dashboard_as_soon_as_it_connects()
    {
        // A support agent opening the dashboard needs the last few minutes of activity, not a
        // blank grid that fills only when the next transaction happens to arrive.
        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);

        await using var dashboard = await DashboardClient.ConnectAsync(_factory);

        var snapshot = await dashboard.SnapshotAsync();
        Assert.Equal([payload.TransactionId], snapshot.Select(t => t.TransactionId));
    }

    [Fact]
    public async Task Sends_an_empty_snapshot_when_nothing_has_happened_yet()
    {
        await using var dashboard = await DashboardClient.ConnectAsync(_factory);

        Assert.Empty(await dashboard.SnapshotAsync());
    }

    [Fact]
    public async Task Pushes_a_transaction_as_it_is_ingested()
    {
        await using var dashboard = await DashboardClient.ConnectAsync(_factory);
        await dashboard.SnapshotAsync();

        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);

        var update = Assert.Single(await dashboard.ReceiveAsync(1));
        Assert.Equal(payload.TransactionId, update.TransactionId);
        Assert.Equal("Pending", update.Status);
        Assert.Equal(1500.50m, update.Amount);
    }

    [Fact]
    public async Task Pushes_a_status_change()
    {
        await using var dashboard = await DashboardClient.ConnectAsync(_factory);
        await dashboard.SnapshotAsync();

        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);
        await _client.PostTransactionAsync(payload.AsFailed());

        var updates = await dashboard.ReceiveAsync(2);
        Assert.Equal(["Pending", "Failed"], updates.Select(u => u.Status));
        Assert.All(updates, u => Assert.Equal(payload.TransactionId, u.TransactionId));
    }

    [Fact]
    public async Task Does_not_push_anything_for_a_retried_post()
    {
        // Re-sending an unchanged transaction must not make the dashboard flash an update for
        // something that did not change.
        await using var dashboard = await DashboardClient.ConnectAsync(_factory);
        await dashboard.SnapshotAsync();

        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);
        await dashboard.ReceiveAsync(1);

        await _client.PostTransactionAsync(payload);

        await dashboard.ExpectNothingFurtherAsync(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Does_not_push_anything_for_a_rejected_update()
    {
        await using var dashboard = await DashboardClient.ConnectAsync(_factory);
        await dashboard.SnapshotAsync();

        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);
        await _client.PostTransactionAsync(payload.AsCompleted());
        await dashboard.ReceiveAsync(2);

        await _client.PostTransactionAsync(payload.AsFailed() with
        {
            Timestamp = payload.Timestamp!.Value.AddMinutes(1),
        });

        await dashboard.ExpectNothingFurtherAsync(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Fans_out_to_every_connected_dashboard()
    {
        // The requirement is many concurrent connections, so the test uses many: each client
        // must receive the broadcast independently, with no interference between them.
        var dashboards = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => DashboardClient.ConnectAsync(_factory)));

        try
        {
            await Task.WhenAll(dashboards.Select(d => d.SnapshotAsync()));

            var payload = TransactionPayload.New();
            await _client.PostTransactionAsync(payload);

            var received = await Task.WhenAll(dashboards.Select(d => d.ReceiveAsync(1)));

            Assert.All(received, updates =>
                Assert.Equal(payload.TransactionId, Assert.Single(updates).TransactionId));
        }
        finally
        {
            foreach (var dashboard in dashboards)
            {
                await dashboard.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Delivers_every_one_of_a_hundred_transactions_arriving_at_once()
    {
        // The headline load case from the requirements. Nothing may be dropped or duplicated
        // when a hundred ingestion requests land concurrently on the same broadcast path.
        await using var dashboard = await DashboardClient.ConnectAsync(_factory);
        await dashboard.SnapshotAsync();

        var payloads = Enumerable.Range(0, 100).Select(_ => TransactionPayload.New()).ToArray();

        var responses = await Task.WhenAll(payloads.Select(_client.PostTransactionAsync));

        foreach (var response in responses)
        {
            response.Dispose();
        }

        var received = await dashboard.ReceiveAsync(100, TimeSpan.FromSeconds(30));

        Assert.Equal(
            payloads.Select(p => p.TransactionId).Order(),
            received.Select(r => r.TransactionId).Order());
    }

    [Fact]
    public async Task Serves_a_late_joiner_the_same_state_an_existing_dashboard_holds()
    {
        // Reconnect behaviour: a dashboard that drops and comes back must not need the page
        // reloading to catch up, and must not be shown a different world from its peers.
        await using var early = await DashboardClient.ConnectAsync(_factory);
        await early.SnapshotAsync();

        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);
        await early.ReceiveAsync(1);

        await using var late = await DashboardClient.ConnectAsync(_factory);
        var snapshot = await late.SnapshotAsync();

        Assert.Equal([payload.TransactionId], snapshot.Select((TransactionDto t) => t.TransactionId));
    }
}
