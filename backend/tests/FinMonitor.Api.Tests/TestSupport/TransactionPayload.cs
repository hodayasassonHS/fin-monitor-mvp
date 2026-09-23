namespace FinMonitor.Api.Tests.TestSupport;

/// <summary>
/// A transaction exactly as a client would send it: loose JSON, not the domain type.
/// </summary>
/// <remarks>
/// Integration tests deliberately build the payload by hand instead of reusing the server's own
/// contract type. Serialising the server's own record would make the test agree with the server
/// by construction and would never catch a breaking change to the published JSON shape.
/// </remarks>
internal sealed record TransactionPayload
{
    public string? TransactionId { get; init; } = Guid.NewGuid().ToString();

    public decimal? Amount { get; init; } = 1500.50m;

    public string? Currency { get; init; } = "USD";

    public string? Status { get; init; } = "Pending";

    public DateTimeOffset? Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public static TransactionPayload New() => new();

    public TransactionPayload AsCompleted() => this with
    {
        Status = "Completed",
        Timestamp = (Timestamp ?? DateTimeOffset.UtcNow).AddSeconds(30),
    };

    public TransactionPayload AsFailed() => this with
    {
        Status = "Failed",
        Timestamp = (Timestamp ?? DateTimeOffset.UtcNow).AddSeconds(30),
    };
}
