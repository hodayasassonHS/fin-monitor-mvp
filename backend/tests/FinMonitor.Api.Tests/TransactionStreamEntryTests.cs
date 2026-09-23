using System.Globalization;
using FinMonitor.Core.Transactions;
using FinMonitor.Redis;
using StackExchange.Redis;

namespace FinMonitor.Api.Tests;

/// <summary>
/// The Redis Stream entry format is the wire contract between replicas.
/// </summary>
/// <remarks>
/// Worth testing carefully despite looking like plumbing: replicas may run different builds
/// during a rolling deployment and under different locales, and a value that does not survive
/// the round trip byte-for-byte would make last-write-wins comparisons differ between them —
/// producing a divergence that no amount of correct merge logic could repair.
/// </remarks>
public sealed class TransactionStreamEntryTests
{
    private static readonly Transaction Sample = new(
        Guid.Parse("6f1d8b6a-6b0f-4f27-8f6f-2a3c4d5e6f70"),
        1500.50m,
        "USD",
        TransactionStatus.Pending,
        new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero));

    private static StreamEntry EntryFor(Transaction transaction) =>
        new("1-0", TransactionStreamEntry.ToEntries(transaction));

    [Fact]
    public void Round_trips_a_transaction_unchanged()
    {
        Assert.True(TransactionStreamEntry.TryParse(EntryFor(Sample), out var parsed));
        Assert.Equal(Sample, parsed);
    }

    [Theory]
    [InlineData(TransactionStatus.Pending)]
    [InlineData(TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed)]
    public void Round_trips_every_status(TransactionStatus status)
    {
        var transaction = Sample with { Status = status };

        Assert.True(TransactionStreamEntry.TryParse(EntryFor(transaction), out var parsed));
        Assert.Equal(status, parsed.Status);
    }

    [Fact]
    public void Preserves_sub_second_precision_in_the_timestamp()
    {
        // Timestamps order the merge. Truncating them would turn genuinely ordered observations
        // into ties and make the winner depend on delivery order instead.
        var precise = Sample with { Timestamp = new DateTimeOffset(2024, 1, 15, 10, 0, 0, 123, TimeSpan.Zero).AddTicks(4567) };

        Assert.True(TransactionStreamEntry.TryParse(EntryFor(precise), out var parsed));
        Assert.Equal(precise.Timestamp, parsed.Timestamp);
    }

    [Fact]
    public void Preserves_decimal_precision_in_the_amount()
    {
        var precise = Sample with { Amount = 0.01m };

        Assert.True(TransactionStreamEntry.TryParse(EntryFor(precise), out var parsed));
        Assert.Equal(0.01m, parsed.Amount);
    }

    [Fact]
    public void Writes_values_that_do_not_depend_on_the_current_culture()
    {
        // A replica running under a locale that uses ',' as the decimal separator must write a
        // value its peers can read. This is the classic way a distributed system quietly breaks
        // when one node is deployed to a differently configured host.
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var entry = EntryFor(Sample);

            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            Assert.True(TransactionStreamEntry.TryParse(entry, out var parsed));
            Assert.Equal(Sample, parsed);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Refuses_an_entry_with_a_missing_field()
    {
        var withoutStatus = TransactionStreamEntry.ToEntries(Sample)
            .Where(field => field.Name != "status")
            .ToArray();

        Assert.False(TransactionStreamEntry.TryParse(new StreamEntry("1-0", withoutStatus), out _));
    }

    [Theory]
    [InlineData("transactionId", "not-a-guid")]
    [InlineData("amount", "abc")]
    [InlineData("status", "Cancelled")]
    [InlineData("timestamp", "yesterday")]
    public void Refuses_an_entry_with_an_unparseable_field(string field, string value)
    {
        // Returning false rather than throwing is what keeps one bad entry from wedging a
        // replica's consumer loop and freezing its live feed.
        var corrupted = TransactionStreamEntry.ToEntries(Sample)
            .Select(entry => entry.Name == field ? new NameValueEntry(field, value) : entry)
            .ToArray();

        Assert.False(TransactionStreamEntry.TryParse(new StreamEntry("1-0", corrupted), out _));
    }

    [Fact]
    public void Refuses_an_entry_with_no_fields_at_all()
    {
        Assert.False(TransactionStreamEntry.TryParse(new StreamEntry("1-0", []), out _));
    }
}
