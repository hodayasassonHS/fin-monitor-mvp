using FinMonitor.Api.Contracts;
using FinMonitor.Core.Transactions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace FinMonitor.Api.Realtime;

/// <summary>
/// The dashboard's real-time endpoint.
/// </summary>
/// <remarks>
/// The hub itself is intentionally almost empty. SignalR creates a new instance per invocation,
/// so a hub is the wrong place to keep state; the connection registry that broadcasts fan out
/// across lives in SignalR's own <c>HubLifetimeManager</c>, which is built to be called from any
/// thread. Everything this class does is hand a new connection its starting state.
/// </remarks>
public sealed class TransactionsHub(
    ITransactionStore store,
    IOptions<TransactionStoreOptions> storeOptions,
    ILogger<TransactionsHub> logger) : Hub<ITransactionsClient>
{
    public override async Task OnConnectedAsync()
    {
        // GetLatest returns an immutable copy, so this snapshot is unaffected by transactions
        // being ingested while it is serialised and sent.
        var snapshot = store.GetLatest(limit: storeOptions.Value.Capacity);

        await Clients.Caller.Snapshot(TransactionDto.From(snapshot)).ConfigureAwait(false);

        logger.LogDebug(
            "Dashboard {ConnectionId} connected; sent {Count} transactions.",
            Context.ConnectionId,
            snapshot.Count);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            logger.LogDebug(exception, "Dashboard {ConnectionId} dropped.", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
