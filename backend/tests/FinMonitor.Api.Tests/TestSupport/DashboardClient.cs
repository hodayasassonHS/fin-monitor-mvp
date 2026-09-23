using System.Net.WebSockets;
using System.Threading.Channels;
using FinMonitor.Api.Contracts;
using FinMonitor.Api.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace FinMonitor.Api.Tests.TestSupport;

/// <summary>
/// A real SignalR client connected to the in-process test server, standing in for the dashboard.
/// </summary>
/// <remarks>
/// Uses the actual SignalR client over the test server's WebSocket transport rather than calling
/// the hub class directly. Invoking the hub in isolation would prove nothing about routing,
/// protocol negotiation or the JSON payload the browser actually receives — which is where the
/// interesting failures live.
/// </remarks>
internal sealed class DashboardClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HubConnection _connection;

    private readonly Channel<TransactionDto> _updates = Channel.CreateUnbounded<TransactionDto>();

    private readonly TaskCompletionSource<IReadOnlyList<TransactionDto>> _snapshot =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DashboardClient(HubConnection connection)
    {
        _connection = connection;

        // Registered before the connection is started so that the snapshot the server sends from
        // OnConnectedAsync cannot arrive before anything is listening for it.
        _connection.On<IReadOnlyList<TransactionDto>>(
            nameof(ITransactionsClient.Snapshot),
            transactions => _snapshot.TrySetResult(transactions));

        _connection.On<TransactionDto>(
            nameof(ITransactionsClient.TransactionUpserted),
            transaction => _updates.Writer.TryWrite(transaction));
    }

    public static async Task<DashboardClient> ConnectAsync(FinMonitorApiFactory factory)
    {
        var server = factory.Server;

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/transactions"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    // TestServer's WebSocket client expects an http scheme; SignalR hands us ws.
                    var uri = new UriBuilder(context.Uri) { Scheme = "http" }.Uri;
                    return await server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
                };
            })
            .Build();

        var client = new DashboardClient(connection);
        await connection.StartAsync();

        return client;
    }

    /// <summary>The state the server pushed when this client connected.</summary>
    public Task<IReadOnlyList<TransactionDto>> SnapshotAsync() => _snapshot.Task.WaitAsync(DefaultTimeout);

    /// <summary>Waits for exactly <paramref name="count"/> live updates, failing on timeout.</summary>
    public async Task<IReadOnlyList<TransactionDto>> ReceiveAsync(int count, TimeSpan? timeout = null)
    {
        using var expiry = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var received = new List<TransactionDto>(count);

        try
        {
            while (received.Count < count)
            {
                received.Add(await _updates.Reader.ReadAsync(expiry.Token));
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Expected {count} updates but received {received.Count} before timing out.");
        }

        return received;
    }

    /// <summary>Asserts that nothing further arrives within <paramref name="window"/>.</summary>
    public async Task ExpectNothingFurtherAsync(TimeSpan window)
    {
        using var expiry = new CancellationTokenSource(window);

        try
        {
            var unexpected = await _updates.Reader.ReadAsync(expiry.Token);
            Assert.Fail($"Received an unexpected update for transaction {unexpected.TransactionId}.");
        }
        catch (OperationCanceledException)
        {
            // Nothing arrived, which is what we wanted.
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
