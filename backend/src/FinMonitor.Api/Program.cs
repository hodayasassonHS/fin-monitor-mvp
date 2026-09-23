using System.Text.Json;
using System.Text.Json.Serialization;
using FinMonitor.Api.Endpoints;
using FinMonitor.Api.Health;
using FinMonitor.Api.Realtime;
using FinMonitor.Core.DependencyInjection;
using FinMonitor.Core.Transactions;
using FinMonitor.Redis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(options => ApplyJsonConventions(options.SerializerOptions));

builder.Services
    .AddSignalR()
    // The hub and the REST API must agree on casing; the dashboard consumes both and should not
    // need two different shapes for the same transaction.
    .AddJsonProtocol(options => ApplyJsonConventions(options.PayloadSerializerOptions));

AddCors(builder);

// ---------------------------------------------------------------------------------------------
// The real-time pipeline. Registration order matters here, which is why it is in one place with
// an explanation rather than scattered through the file.
// ---------------------------------------------------------------------------------------------

// Registered first so that it is the first hosted service to start. IHostedService instances
// start in registration order, and this one installs the subscription that applies transactions
// to the store. The Redis consumer below begins replaying the stream as soon as *it* starts, so
// if the order were reversed the replay would be delivered to nobody and the replica would come
// up with an empty view.
builder.Services.AddHostedService<TransactionProjectionService>();

// Before AddTransactionCore: this replaces the in-process bus, which Core registers with
// TryAdd. A no-op unless a Redis connection string is configured.
builder.Services.AddRedisTransactionBus(
    options => builder.Configuration.GetSection(RedisTransactionBusOptions.SectionName).Bind(options));

builder.Services.AddTransactionCore();
builder.Services.Configure<TransactionStoreOptions>(
    builder.Configuration.GetSection(TransactionStoreOptions.SectionName));

builder.Services.AddHealthChecks()
    .AddCheck<RedisConnectionHealthCheck>("transaction-stream", tags: ["ready"]);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

// Names the replica that served each response. With several replicas behind one Service this is
// the difference between "the dashboard is missing a transaction" and "replica 3 is missing a
// transaction" — and it is what makes the cross-replica behaviour observable in the compose demo.
var instanceName = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;

app.Use((context, next) =>
{
    context.Response.Headers["X-Instance"] = instanceName;
    return next(context);
});

app.MapTransactionEndpoints();
app.MapHub<TransactionsHub>("/hubs/transactions");

// Liveness answers "is this process wedged?", so it runs no dependency checks at all. A replica
// that has lost Redis is doing its job on stale data and must not be killed and restarted for it.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
});

app.Run();

static void ApplyJsonConventions(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.PropertyNameCaseInsensitive = true;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
}

static void AddCors(WebApplicationBuilder builder)
{
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            // Same-origin deployment (the dashboard served behind the same ingress). No cross-
            // origin access is granted at all rather than falling back to a permissive default.
            policy.WithOrigins([]);
            return;
        }

        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            // Required by SignalR, and the reason origins are listed explicitly: a wildcard
            // origin is not permitted alongside credentials.
            .AllowCredentials();
    }));
}

/// <summary>Exposed so the integration tests can host this application with WebApplicationFactory.</summary>
public partial class Program;
