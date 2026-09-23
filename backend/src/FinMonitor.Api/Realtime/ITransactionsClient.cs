using FinMonitor.Api.Contracts;

namespace FinMonitor.Api.Realtime;

/// <summary>
/// The methods the server can call on a connected dashboard.
/// </summary>
/// <remarks>
/// Declaring the client contract as an interface makes the hub strongly typed: method names are
/// checked by the compiler instead of being magic strings that fail silently at runtime when
/// renamed on one side only.
/// </remarks>
public interface ITransactionsClient
{
    /// <summary>
    /// Sent once, immediately on connect, with the transactions this replica already holds.
    /// </summary>
    /// <remarks>
    /// Delivered over the hub rather than left to a separate REST call on purpose. Fetching a
    /// snapshot over HTTP and then subscribing leaves a window in which transactions arrive
    /// after the snapshot was taken but before the subscription exists, and those are lost.
    /// Sending it on the connection itself closes the gap, because SignalR preserves ordering
    /// within a connection: nothing can slip between this message and the updates that follow.
    /// </remarks>
    Task Snapshot(IReadOnlyList<TransactionDto> transactions);

    /// <summary>Sent when a transaction is created or its status changes.</summary>
    Task TransactionUpserted(TransactionDto transaction);
}
