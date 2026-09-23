using FinMonitor.Api.Contracts;
using FinMonitor.Core.Transactions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace FinMonitor.Api.Endpoints;

public static class TransactionEndpoints
{
    /// <summary>Largest page the read endpoint will serve, whatever the caller asks for.</summary>
    private const int MaxLimit = 1000;

    private const int DefaultLimit = 100;

    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        group.MapPost("/", IngestAsync)
            .WithName("IngestTransaction")
            .WithSummary("Ingests a transaction, or updates the status of one already known.");

        group.MapGet("/", GetLatest)
            .WithName("GetLatestTransactions")
            .WithSummary("Returns the most recently updated transactions, newest first.");

        group.MapGet("/{transactionId:guid}", GetById)
            .WithName("GetTransaction")
            .WithSummary("Returns a single transaction by id.");

        return app;
    }

    /// <remarks>
    /// Returns <c>202 Accepted</c> rather than <c>201 Created</c>. The transaction has been
    /// published to the cluster but, when running on more than one replica, has not necessarily
    /// been applied everywhere yet — 202 says exactly that, where 201 would promise a resource
    /// that a read against another replica might not find for a few milliseconds.
    /// </remarks>
    private static async Task<Results<Accepted<TransactionDto>, Ok<TransactionDto>, ValidationProblem, ProblemHttpResult>>
        IngestAsync(
            TransactionIngestRequest request,
            TransactionIngestionService ingestion,
            CancellationToken cancellationToken)
    {
        var result = await ingestion.IngestAsync(request, cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            IngestionStatus.Accepted => TypedResults.Accepted(
                $"/api/transactions/{result.Transaction!.TransactionId:D}",
                TransactionDto.From(result.Transaction)),

            // A retry of something already recorded is a success. Answering 409 would teach
            // callers to treat a safe retry as a failure.
            IngestionStatus.Duplicate => TypedResults.Ok(TransactionDto.From(result.Transaction!)),

            IngestionStatus.Invalid => TypedResults.ValidationProblem(
                result.Errors
                    .GroupBy(error => error.Field, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(error => error.Message).ToArray(),
                        StringComparer.Ordinal),
                detail: "The transaction payload is not valid."),

            IngestionStatus.Conflict => Conflict(result),

            _ => throw new InvalidOperationException($"Unhandled ingestion status '{result.Status}'."),
        };
    }

    private static ProblemHttpResult Conflict(IngestionResult result) => TypedResults.Problem(
        title: "Conflicts with the recorded transaction.",
        detail: result.RejectionReason switch
        {
            MergeRejectionReason.TerminalStatus =>
                $"Transaction is already '{result.Transaction!.Status}', which is final.",
            MergeRejectionReason.ImmutableFieldChanged =>
                "Amount and currency cannot change after a transaction is first recorded.",
            MergeRejectionReason.StaleTimestamp =>
                "A more recent update for this transaction has already been recorded.",
            _ => "The update was refused.",
        },
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = result.RejectionReason?.ToString(),
            ["current"] = TransactionDto.From(result.Transaction!),
        });

    private static Results<Ok<IReadOnlyList<TransactionDto>>, ValidationProblem> GetLatest(
        ITransactionStore store,
        IOptions<TransactionStoreOptions> storeOptions,
        string? status,
        int? limit)
    {
        TransactionStatus? parsedStatus = null;

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TransactionStatus>(status, ignoreCase: true, out var candidate) ||
                !Enum.IsDefined(candidate) ||
                !status.All(char.IsAsciiLetter))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["status"] = ["Must be one of 'Pending', 'Completed' or 'Failed'."],
                });
            }

            parsedStatus = candidate;
        }

        if (limit is < 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["limit"] = ["Must not be negative."],
            });
        }

        // Clamped rather than rejected: the store is bounded anyway, so an oversized request is
        // harmless and there is no value in failing it.
        var effectiveLimit = Math.Min(limit ?? DefaultLimit, Math.Min(MaxLimit, storeOptions.Value.Capacity));

        return TypedResults.Ok(TransactionDto.From(store.GetLatest(effectiveLimit, parsedStatus)));
    }

    private static Results<Ok<TransactionDto>, NotFound> GetById(Guid transactionId, ITransactionStore store)
    {
        var transaction = store.Find(transactionId);

        return transaction is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(TransactionDto.From(transaction));
    }
}
