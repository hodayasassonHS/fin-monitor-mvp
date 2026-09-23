using FinMonitor.Core.Tests.TestSupport;
using FinMonitor.Core.Transactions;

namespace FinMonitor.Core.Tests.Transactions;

public sealed class TransactionValidatorTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FixedTimeProvider _clock = new(Now);

    private TransactionValidator Validator => new(_clock);

    private static TransactionIngestRequest ValidRequest() => TransactionBuilder.A().ToRequest();

    [Fact]
    public void Accepts_the_payload_from_the_specification()
    {
        var request = new TransactionIngestRequest
        {
            TransactionId = "6f1d8b6a-6b0f-4f27-8f6f-2a3c4d5e6f70",
            Amount = 1500.50m,
            Currency = "USD",
            Status = "Pending",
            Timestamp = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
        };

        var result = Validator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(Guid.Parse("6f1d8b6a-6b0f-4f27-8f6f-2a3c4d5e6f70"), result.Value!.TransactionId);
        Assert.Equal(1500.50m, result.Value.Amount);
        Assert.Equal("USD", result.Value.Currency);
        Assert.Equal(TransactionStatus.Pending, result.Value.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Rejects_an_unusable_transaction_id(string? id)
    {
        var result = Validator.Validate(ValidRequest() with { TransactionId = id });

        AssertRejects(result, "transactionId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-0.01d)]
    [InlineData(-1500.50d)]
    public void Rejects_a_non_positive_amount(double? amount)
    {
        var result = Validator.Validate(ValidRequest() with { Amount = (decimal?)amount });

        AssertRejects(result, "amount");
    }

    [Fact]
    public void Rejects_an_implausibly_large_amount()
    {
        var result = Validator.Validate(ValidRequest() with { Amount = 1_000_000_001m });

        AssertRejects(result, "amount");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    [InlineData("$$$")]
    public void Rejects_a_currency_that_is_not_an_iso_4217_code(string? currency)
    {
        var result = Validator.Validate(ValidRequest() with { Currency = currency });

        AssertRejects(result, "currency");
    }

    [Theory]
    [InlineData("usd")]
    [InlineData(" Usd ")]
    public void Normalises_currency_casing_and_whitespace(string currency)
    {
        // Two clients spelling the same currency differently must produce values that compare
        // equal, or the immutable-field guard in the merge policy would fire on a real update.
        var result = Validator.Validate(ValidRequest() with { Currency = currency });

        Assert.True(result.IsValid);
        Assert.Equal("USD", result.Value!.Currency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("1")]
    public void Rejects_an_unrecognised_status(string? status)
    {
        var result = Validator.Validate(ValidRequest() with { Status = status });

        AssertRejects(result, "status");
    }

    [Theory]
    [InlineData("completed", TransactionStatus.Completed)]
    [InlineData("FAILED", TransactionStatus.Failed)]
    [InlineData(" Pending ", TransactionStatus.Pending)]
    public void Accepts_status_in_any_casing(string status, TransactionStatus expected)
    {
        var result = Validator.Validate(ValidRequest() with { Status = status });

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Value!.Status);
    }

    [Fact]
    public void Rejects_a_missing_timestamp()
    {
        var result = Validator.Validate(ValidRequest() with { Timestamp = null });

        AssertRejects(result, "timestamp");
    }

    [Fact]
    public void Rejects_a_timestamp_far_in_the_future()
    {
        // A future-dated event would win last-write-wins against every later observation and
        // freeze the transaction in that state for good.
        var result = Validator.Validate(ValidRequest() with { Timestamp = Now.AddHours(1) });

        AssertRejects(result, "timestamp");
    }

    [Fact]
    public void Tolerates_a_timestamp_slightly_ahead_of_the_server_clock()
    {
        var result = Validator.Validate(ValidRequest() with { Timestamp = Now.AddMinutes(1) });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Normalises_the_timestamp_to_utc()
    {
        var local = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.FromHours(3));

        var result = Validator.Validate(ValidRequest() with { Timestamp = local });

        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.Zero, result.Value!.Timestamp.Offset);
        Assert.Equal(local.UtcDateTime, result.Value.Timestamp.UtcDateTime);
    }

    [Fact]
    public void Reports_every_bad_field_at_once()
    {
        // Returning only the first problem would make a caller fix and resubmit one field at a time.
        var result = Validator.Validate(new TransactionIngestRequest());

        Assert.False(result.IsValid);
        Assert.Equal(
            ["amount", "currency", "status", "timestamp", "transactionId"],
            result.Errors.Select(e => e.Field).Order());
    }

    private static void AssertRejects(ValidationResult result, string expectedField)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Field == expectedField);
    }
}
