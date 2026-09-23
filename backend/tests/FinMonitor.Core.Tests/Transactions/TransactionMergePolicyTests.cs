using FinMonitor.Core.Tests.TestSupport;
using FinMonitor.Core.Transactions;

namespace FinMonitor.Core.Tests.Transactions;

/// <summary>
/// The merge policy decides what "the truth" is when two observations of the same transaction
/// disagree. Every replica runs these same rules over the same log, so these tests are also the
/// specification for how replicas converge.
/// </summary>
public sealed class TransactionMergePolicyTests
{
    [Fact]
    public void Accepts_a_transaction_id_seen_for_the_first_time()
    {
        var incoming = TransactionBuilder.A().Build();

        var result = TransactionMergePolicy.Merge(existing: null, incoming);

        Assert.True(result.Accepted);
        Assert.Same(incoming, result.Value);
    }

    [Theory]
    [InlineData(TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed)]
    public void Accepts_a_pending_transaction_moving_to_a_terminal_status(TransactionStatus terminal)
    {
        var builder = TransactionBuilder.A();
        var existing = builder.Build();
        var incoming = builder.WithStatus(terminal).SecondsLater(30).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.True(result.Accepted);
        Assert.Equal(terminal, result.Value.Status);
    }

    [Fact]
    public void Accepts_an_update_carrying_the_same_timestamp_as_the_stored_one()
    {
        // Two observations can share an instant at the resolution we store. That is not enough
        // reason to drop a genuine status change, so equal timestamps still merge.
        var builder = TransactionBuilder.A();
        var existing = builder.Build();
        var incoming = builder.WithStatus(TransactionStatus.Completed).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.True(result.Accepted);
        Assert.Equal(TransactionStatus.Completed, result.Value.Status);
    }

    [Fact]
    public void Rejects_an_identical_resend_as_a_duplicate()
    {
        var existing = TransactionBuilder.A().Build();

        var result = TransactionMergePolicy.Merge(existing, existing);

        Assert.False(result.Accepted);
        Assert.Equal(MergeRejectionReason.Duplicate, result.RejectionReason);
        Assert.Same(existing, result.Value);
    }

    [Fact]
    public void Rejects_a_repeat_of_the_same_status_at_a_later_time_as_a_duplicate()
    {
        // Idempotency has to survive re-delivery, and a redelivered event may well carry a newer
        // observation time without representing any actual change of state.
        var builder = TransactionBuilder.A();
        var existing = builder.Build();
        var incoming = builder.SecondsLater(60).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.False(result.Accepted);
        Assert.Equal(MergeRejectionReason.Duplicate, result.RejectionReason);
    }

    [Fact]
    public void Rejects_an_observation_older_than_the_stored_one()
    {
        var builder = TransactionBuilder.A().WithTimestamp(TransactionBuilder.T0.AddMinutes(5));
        var existing = builder.WithStatus(TransactionStatus.Completed).Build();
        var incoming = builder.WithStatus(TransactionStatus.Pending).SecondsLater(-60).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.False(result.Accepted);
        Assert.Equal(MergeRejectionReason.StaleTimestamp, result.RejectionReason);
        Assert.Equal(TransactionStatus.Completed, result.Value.Status);
    }

    [Theory]
    [InlineData(TransactionStatus.Completed, TransactionStatus.Failed)]
    [InlineData(TransactionStatus.Completed, TransactionStatus.Pending)]
    [InlineData(TransactionStatus.Failed, TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed, TransactionStatus.Pending)]
    public void Rejects_any_move_out_of_a_terminal_status(TransactionStatus from, TransactionStatus to)
    {
        var builder = TransactionBuilder.A();
        var existing = builder.WithStatus(from).Build();
        var incoming = builder.WithStatus(to).SecondsLater(30).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.False(result.Accepted);
        Assert.Equal(MergeRejectionReason.TerminalStatus, result.RejectionReason);
        Assert.Equal(from, result.Value.Status);
    }

    [Fact]
    public void Rejects_an_update_that_changes_the_amount()
    {
        var builder = TransactionBuilder.A();
        var existing = builder.Build();
        var incoming = builder.WithAmount(9999m).WithStatus(TransactionStatus.Completed).SecondsLater(30).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.False(result.Accepted);
        Assert.Equal(MergeRejectionReason.ImmutableFieldChanged, result.RejectionReason);
        Assert.Equal(1500.50m, result.Value.Amount);
    }

    [Fact]
    public void Rejects_an_update_that_changes_the_currency()
    {
        var builder = TransactionBuilder.A();
        var existing = builder.Build();
        var incoming = builder.WithCurrency("EUR").WithStatus(TransactionStatus.Completed).SecondsLater(30).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.False(result.Accepted);
        Assert.Equal(MergeRejectionReason.ImmutableFieldChanged, result.RejectionReason);
        Assert.Equal("USD", result.Value.Currency);
    }

    [Fact]
    public void Treats_amounts_that_differ_only_in_scale_as_equal()
    {
        // 1500.50 and 1500.5000 are the same sum of money. Tripping the immutable-field guard on
        // a difference in trailing zeros would reject legitimate updates from any client that
        // serialises decimals differently.
        var builder = TransactionBuilder.A().WithAmount(1500.50m);
        var existing = builder.Build();
        var incoming = builder.WithAmount(1500.5000m).WithStatus(TransactionStatus.Completed).Build();

        var result = TransactionMergePolicy.Merge(existing, incoming);

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Refuses_to_merge_two_different_transactions()
    {
        var existing = TransactionBuilder.A().Build();
        var unrelated = TransactionBuilder.A().Build();

        Assert.Throws<ArgumentException>(() => TransactionMergePolicy.Merge(existing, unrelated));
    }

    /// <summary>
    /// The convergence property the distributed design rests on: applying the same set of events
    /// in any order lands on the same state, so replicas that replay a log cannot disagree.
    /// </summary>
    [Fact]
    public void Converges_on_the_same_state_regardless_of_the_order_events_are_applied()
    {
        var builder = TransactionBuilder.A();
        var pending = builder.Build();
        var completed = builder.WithStatus(TransactionStatus.Completed).SecondsLater(30).Build();

        var forwards = Fold(pending, completed);
        var backwards = Fold(completed, pending);

        Assert.Equal(completed, forwards);
        Assert.Equal(completed, backwards);

        static Transaction Fold(params Transaction[] events)
        {
            Transaction? state = null;

            foreach (var next in events)
            {
                var result = TransactionMergePolicy.Merge(state, next);
                state = result.Accepted ? result.Value : state;
            }

            return state!;
        }
    }
}
