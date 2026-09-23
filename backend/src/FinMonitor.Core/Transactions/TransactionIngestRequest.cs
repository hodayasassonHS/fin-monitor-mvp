namespace FinMonitor.Core.Transactions;

/// <summary>
/// The raw, untrusted transaction payload as it arrives over HTTP.
/// </summary>
/// <remarks>
/// Every property is nullable and loosely typed on purpose. Binding straight onto strongly
/// typed members would let the JSON deserialiser decide what a malformed request means, and it
/// reports those failures as opaque 400s. Parsing happens in <see cref="TransactionValidator"/>
/// instead, so every rejection carries a field name and a message the caller can act on.
/// </remarks>
public sealed record TransactionIngestRequest
{
    public string? TransactionId { get; init; }

    public decimal? Amount { get; init; }

    public string? Currency { get; init; }

    public string? Status { get; init; }

    public DateTimeOffset? Timestamp { get; init; }
}
