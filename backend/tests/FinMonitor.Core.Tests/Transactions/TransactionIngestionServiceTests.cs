using FinMonitor.Core.Tests.TestSupport;
using FinMonitor.Core.Transactions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FinMonitor.Core.Tests.Transactions;

public sealed class TransactionIngestionServiceTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryTransactionStore _store =
        new(Options.Create(new TransactionStoreOptions { Capacity = 100 }));

    private readonly RecordingTransactionEventBus _bus = new();

    private TransactionIngestionService Service => new(
        new TransactionValidator(new FixedTimeProvider(Now)),
        _store,
        _bus,
        NullLogger<TransactionIngestionService>.Instance);

    [Fact]
    public async Task Publishes_a_valid_transaction()
    {
        var builder = TransactionBuilder.A();

        var result = await Service.IngestAsync(builder.ToRequest());

        Assert.Equal(IngestionStatus.Accepted, result.Status);
        Assert.Equal(builder.Build(), result.Transaction);
        Assert.Equal([builder.Build()], _bus.Published);
    }

    [Fact]
    public async Task Publishes_the_normalised_transaction_rather_than_the_raw_payload()
    {
        // Downstream replicas receive whatever is published, so normalisation has to happen
        // before the event leaves this process, not when it is applied.
        var request = TransactionBuilder.A().ToRequest() with { Currency = "usd", Status = "completed" };

        await Service.IngestAsync(request);

        var published = Assert.Single(_bus.Published);
        Assert.Equal("USD", published.Currency);
        Assert.Equal(TransactionStatus.Completed, published.Status);
    }

    [Fact]
    public async Task Rejects_an_invalid_payload_without_publishing()
    {
        // Malformed input must not reach the rest of the cluster; every replica would then have
        // to defend against it independently.
        var result = await Service.IngestAsync(new TransactionIngestRequest());

        Assert.Equal(IngestionStatus.Invalid, result.Status);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.Transaction);
        Assert.Empty(_bus.Published);
    }

    [Fact]
    public async Task Treats_a_resent_transaction_as_a_duplicate_rather_than_an_error()
    {
        // A client retrying after a timeout is doing the right thing. Reporting that as a
        // failure would push callers towards unsafe retry behaviour.
        var builder = TransactionBuilder.A();
        _store.Apply(builder.Build());

        var result = await Service.IngestAsync(builder.ToRequest());

        Assert.Equal(IngestionStatus.Duplicate, result.Status);
        Assert.Equal(builder.Build(), result.Transaction);
        Assert.Empty(_bus.Published);
    }

    [Fact]
    public async Task Reports_a_conflict_when_the_transaction_has_already_settled()
    {
        var builder = TransactionBuilder.A().WithStatus(TransactionStatus.Completed);
        _store.Apply(builder.Build());

        var result = await Service.IngestAsync(
            builder.WithStatus(TransactionStatus.Failed).SecondsLater(30).ToRequest());

        Assert.Equal(IngestionStatus.Conflict, result.Status);
        Assert.Equal(MergeRejectionReason.TerminalStatus, result.RejectionReason);
        Assert.Equal(TransactionStatus.Completed, result.Transaction!.Status);
        Assert.Empty(_bus.Published);
    }

    [Fact]
    public async Task Reports_a_conflict_when_the_amount_disagrees_with_what_was_recorded()
    {
        var builder = TransactionBuilder.A();
        _store.Apply(builder.Build());

        var result = await Service.IngestAsync(
            builder.WithAmount(1m).WithStatus(TransactionStatus.Completed).SecondsLater(30).ToRequest());

        Assert.Equal(IngestionStatus.Conflict, result.Status);
        Assert.Equal(MergeRejectionReason.ImmutableFieldChanged, result.RejectionReason);
        Assert.Empty(_bus.Published);
    }

    [Fact]
    public async Task Publishes_a_legitimate_status_update()
    {
        var builder = TransactionBuilder.A();
        _store.Apply(builder.Build());

        var result = await Service.IngestAsync(
            builder.WithStatus(TransactionStatus.Completed).SecondsLater(30).ToRequest());

        Assert.Equal(IngestionStatus.Accepted, result.Status);
        Assert.Equal(TransactionStatus.Completed, Assert.Single(_bus.Published).Status);
    }

    [Fact]
    public async Task Never_writes_to_the_store_itself()
    {
        // The store has exactly one writer — the bus subscriber. If ingestion also wrote
        // directly, the local replica would diverge from every remote one.
        await Service.IngestAsync(TransactionBuilder.A().ToRequest());

        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public async Task Applies_the_transaction_locally_once_the_bus_is_wired_up()
    {
        // The production wiring, end to end: publish reaches a subscriber that applies to the
        // store, so a read issued after ingestion returns sees the transaction.
        var bus = new InProcessTransactionEventBus();
        using var _ = bus.Subscribe((transaction, _) =>
        {
            _store.Apply(transaction);
            return Task.CompletedTask;
        });

        var service = new TransactionIngestionService(
            new TransactionValidator(new FixedTimeProvider(Now)),
            _store,
            bus,
            NullLogger<TransactionIngestionService>.Instance);

        var builder = TransactionBuilder.A();
        await service.IngestAsync(builder.ToRequest());

        Assert.Equal(builder.Build(), _store.Find(builder.TransactionId));
    }
}
