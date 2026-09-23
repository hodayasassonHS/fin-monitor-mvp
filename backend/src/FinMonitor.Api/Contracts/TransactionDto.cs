using FinMonitor.Core.Transactions;

namespace FinMonitor.Api.Contracts;

/// <summary>
/// The wire representation of a transaction, matching the payload in the specification.
/// </summary>
/// <remarks>
/// Kept separate from the domain <see cref="Transaction"/> so the published contract does not
/// move every time the domain does. <c>Status</c> is a string rather than the enum: the JSON
/// contract is the status <i>name</i>, and serialising the enum would risk leaking its numeric
/// value to clients if the serialiser were ever reconfigured.
/// </remarks>
public sealed record TransactionDto(
    string TransactionId,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset Timestamp)
{
    public static TransactionDto From(Transaction transaction) => new(
        transaction.TransactionId.ToString("D"),
        transaction.Amount,
        transaction.Currency,
        transaction.Status.ToString(),
        transaction.Timestamp);

    public static IReadOnlyList<TransactionDto> From(IReadOnlyList<Transaction> transactions)
    {
        var results = new TransactionDto[transactions.Count];

        for (var i = 0; i < transactions.Count; i++)
        {
            results[i] = From(transactions[i]);
        }

        return results;
    }
}
