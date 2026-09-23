using System.Globalization;
using FinMonitor.Core.Transactions;
using StackExchange.Redis;

namespace FinMonitor.Redis;

/// <summary>
/// Maps a <see cref="Transaction"/> to and from the field/value pairs of a Redis Stream entry.
/// </summary>
/// <remarks>
/// Fields are written individually rather than as one blob of JSON. The stream is the system's
/// source of truth, and being able to read it with <c>XRANGE</c> from <c>redis-cli</c> during an
/// incident is worth more than the convenience of a serialiser.
/// <para>
/// Every value is formatted with the invariant culture and round-trip formats. A replica running
/// under a different locale must read back byte-identical values, or last-write-wins comparisons
/// would differ between replicas.
/// </para>
/// </remarks>
internal static class TransactionStreamEntry
{
    private const string TransactionIdField = "transactionId";
    private const string AmountField = "amount";
    private const string CurrencyField = "currency";
    private const string StatusField = "status";
    private const string TimestampField = "timestamp";

    public static NameValueEntry[] ToEntries(Transaction transaction) =>
    [
        new(TransactionIdField, transaction.TransactionId.ToString("D", CultureInfo.InvariantCulture)),
        new(AmountField, transaction.Amount.ToString(CultureInfo.InvariantCulture)),
        new(CurrencyField, transaction.Currency),
        new(StatusField, transaction.Status.ToString()),

        // Round-trip ("O") preserves sub-second precision; a lossy format would collapse
        // observations that are genuinely ordered into ties.
        new(TimestampField, transaction.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
    ];

    /// <summary>
    /// Parses a stream entry, returning <c>false</c> for anything malformed.
    /// </summary>
    /// <remarks>
    /// Deliberately total rather than throwing. A single unparseable entry — written by an older
    /// or newer build, or by hand during debugging — must not be able to wedge a replica's
    /// consumer loop and stall its live feed. The caller logs and skips.
    /// </remarks>
    public static bool TryParse(StreamEntry entry, out Transaction transaction)
    {
        transaction = null!;

        var values = entry.Values;

        if (!TryGet(values, TransactionIdField, out var rawId) ||
            !TryGet(values, AmountField, out var rawAmount) ||
            !TryGet(values, CurrencyField, out var rawCurrency) ||
            !TryGet(values, StatusField, out var rawStatus) ||
            !TryGet(values, TimestampField, out var rawTimestamp))
        {
            return false;
        }

        if (!Guid.TryParse(rawId, CultureInfo.InvariantCulture, out var id) ||
            !decimal.TryParse(rawAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ||
            !Enum.TryParse<TransactionStatus>(rawStatus, ignoreCase: false, out var status) ||
            !Enum.IsDefined(status) ||
            !DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return false;
        }

        transaction = new Transaction(id, amount, rawCurrency, status, timestamp.ToUniversalTime());
        return true;
    }

    private static bool TryGet(NameValueEntry[] values, string name, out string value)
    {
        foreach (var candidate in values)
        {
            if (candidate.Name == name)
            {
                var raw = candidate.Value;

                if (raw.IsNullOrEmpty)
                {
                    break;
                }

                value = raw!;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
