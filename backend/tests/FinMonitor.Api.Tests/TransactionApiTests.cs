using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinMonitor.Api.Contracts;
using FinMonitor.Api.Tests.TestSupport;

namespace FinMonitor.Api.Tests;

public sealed class TransactionApiTests : IAsyncLifetime
{
    private readonly FinMonitorApiFactory _factory = new();

    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Accepts_a_well_formed_transaction()
    {
        var payload = TransactionPayload.New();

        var response = await _client.PostTransactionAsync(payload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.ReadAsync<TransactionDto>();
        Assert.Equal(payload.TransactionId, body.TransactionId);
        Assert.Equal(1500.50m, body.Amount);
        Assert.Equal("USD", body.Currency);
        Assert.Equal("Pending", body.Status);
    }

    [Fact]
    public async Task Publishes_the_json_shape_the_specification_defines()
    {
        // Guards the contract the dashboard is written against: camelCase names, status as a
        // string rather than the enum's ordinal, amount as a JSON number.
        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);

        var json = await _client.GetStringAsync($"/api/transactions/{payload.TransactionId}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(payload.TransactionId, root.GetProperty("transactionId").GetString());
        Assert.Equal(1500.50m, root.GetProperty("amount").GetDecimal());
        Assert.Equal("USD", root.GetProperty("currency").GetString());
        Assert.Equal("Pending", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("timestamp").ValueKind);
    }

    [Fact]
    public async Task Points_at_the_created_transaction_in_the_location_header()
    {
        var payload = TransactionPayload.New();

        var response = await _client.PostTransactionAsync(payload);

        Assert.Equal($"/api/transactions/{payload.TransactionId}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Rejects_an_empty_payload_and_names_every_missing_field()
    {
        var response = await _client.PostAsync(
            "/api/transactions",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.ReadAsync<JsonElement>();
        var errors = problem.GetProperty("errors");

        foreach (var field in (string[])["transactionId", "amount", "currency", "status", "timestamp"])
        {
            Assert.True(errors.TryGetProperty(field, out _), $"Expected an error for '{field}'.");
        }
    }

    [Fact]
    public async Task Rejects_an_unknown_status()
    {
        var response = await _client.PostTransactionAsync(TransactionPayload.New() with { Status = "Cancelled" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Treats_a_retried_post_as_a_success()
    {
        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);

        var retry = await _client.PostTransactionAsync(payload);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Accepts_a_status_update_for_a_known_transaction()
    {
        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);

        var update = await _client.PostTransactionAsync(payload.AsCompleted());

        Assert.Equal(HttpStatusCode.Accepted, update.StatusCode);

        var current = await _client.GetFromJsonAsync<TransactionDto>($"/api/transactions/{payload.TransactionId}");
        Assert.Equal("Completed", current!.Status);
    }

    [Fact]
    public async Task Refuses_to_move_a_settled_transaction_and_says_why()
    {
        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);
        await _client.PostTransactionAsync(payload.AsCompleted());

        var response = await _client.PostTransactionAsync(payload.AsFailed() with
        {
            Timestamp = payload.Timestamp!.Value.AddMinutes(1),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.ReadAsync<JsonElement>();
        Assert.Equal("TerminalStatus", problem.GetProperty("reason").GetString());
        Assert.Equal("Completed", problem.GetProperty("current").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Refuses_an_update_that_rewrites_the_amount()
    {
        var payload = TransactionPayload.New();
        await _client.PostTransactionAsync(payload);

        var response = await _client.PostTransactionAsync(payload.AsCompleted() with { Amount = 1m });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.ReadAsync<JsonElement>();
        Assert.Equal("ImmutableFieldChanged", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Returns_transactions_newest_first()
    {
        var first = TransactionPayload.New();
        var second = TransactionPayload.New();
        await _client.PostTransactionAsync(first);
        await _client.PostTransactionAsync(second);

        var latest = await _client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions");

        Assert.Equal([second.TransactionId, first.TransactionId], latest!.Select(t => t.TransactionId));
    }

    [Fact]
    public async Task Filters_by_status()
    {
        var failed = TransactionPayload.New();
        await _client.PostTransactionAsync(TransactionPayload.New());
        await _client.PostTransactionAsync(failed);
        await _client.PostTransactionAsync(failed.AsFailed());

        var errors = await _client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions?status=Failed");

        Assert.Equal([failed.TransactionId], errors!.Select(t => t.TransactionId));
    }

    [Fact]
    public async Task Rejects_an_unknown_status_filter()
    {
        var response = await _client.GetAsync("/api/transactions?status=Nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Honours_the_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _client.PostTransactionAsync(TransactionPayload.New());
        }

        var page = await _client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions?limit=2");

        Assert.Equal(2, page!.Count);
    }

    [Fact]
    public async Task Reports_an_unknown_transaction_as_not_found()
    {
        var response = await _client.GetAsync($"/api/transactions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Keeps_only_the_most_recent_transactions()
    {
        // The store is a bounded window, and the API must not pretend otherwise.
        await using var factory = new FinMonitorApiFactory { Capacity = 5 };
        using var client = factory.CreateClient();

        for (var i = 0; i < 12; i++)
        {
            await client.PostTransactionAsync(TransactionPayload.New());
        }

        var all = await client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions");

        Assert.Equal(5, all!.Count);
    }

    [Fact]
    public async Task Loses_nothing_when_a_hundred_transactions_are_posted_at_once()
    {
        var payloads = Enumerable.Range(0, 100).Select(_ => TransactionPayload.New()).ToArray();

        var responses = await Task.WhenAll(payloads.Select(_client.PostTransactionAsync));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(100, await CountAsync());

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Reports_healthy(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<int> CountAsync()
    {
        var all = await _client.GetFromJsonAsync<List<TransactionDto>>("/api/transactions?limit=1000");
        return all!.Count;
    }
}
