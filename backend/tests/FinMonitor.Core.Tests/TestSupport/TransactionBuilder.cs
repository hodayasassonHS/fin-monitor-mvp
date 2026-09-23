using FinMonitor.Core.Transactions;

namespace FinMonitor.Core.Tests.TestSupport;

/// <summary>
/// Builds transactions for tests, so each test states only the fields it actually cares about.
/// </summary>
internal sealed record TransactionBuilder
{
    /// <summary>The instant from the requirements' sample payload, used as the default.</summary>
    public static readonly DateTimeOffset T0 = new(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public Guid TransactionId { get; init; } = Guid.NewGuid();

    public decimal Amount { get; init; } = 1500.50m;

    public string Currency { get; init; } = "USD";

    public TransactionStatus Status { get; init; } = TransactionStatus.Pending;

    public DateTimeOffset Timestamp { get; init; } = T0;

    public static TransactionBuilder A() => new();

    public TransactionBuilder WithId(Guid id) => this with { TransactionId = id };

    public TransactionBuilder WithAmount(decimal amount) => this with { Amount = amount };

    public TransactionBuilder WithCurrency(string currency) => this with { Currency = currency };

    public TransactionBuilder WithStatus(TransactionStatus status) => this with { Status = status };

    public TransactionBuilder WithTimestamp(DateTimeOffset timestamp) => this with { Timestamp = timestamp };

    /// <summary>Shifts the timestamp forward, for expressing "a later observation of the same transaction".</summary>
    public TransactionBuilder SecondsLater(int seconds) => this with { Timestamp = Timestamp.AddSeconds(seconds) };

    public Transaction Build() => new(TransactionId, Amount, Currency, Status, Timestamp);

    public TransactionIngestRequest ToRequest() => new()
    {
        TransactionId = TransactionId.ToString(),
        Amount = Amount,
        Currency = Currency,
        Status = Status.ToString(),
        Timestamp = Timestamp,
    };
}
