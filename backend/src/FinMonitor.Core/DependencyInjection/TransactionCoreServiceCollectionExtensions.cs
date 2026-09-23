using FinMonitor.Core.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinMonitor.Core.DependencyInjection;

public static class TransactionCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the transaction domain: validation, the bounded store, the ingestion service
    /// and a single-process event bus.
    /// </summary>
    /// <remarks>
    /// The bus is registered with <c>TryAdd</c> so a transport-specific package can replace it.
    /// Callers that want the distributed bus register it <i>before</i> calling this method.
    /// </remarks>
    public static IServiceCollection AddTransactionCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TransactionStoreOptions>()
            .Validate(
                options => options.Capacity is >= TransactionStoreOptions.MinCapacity
                                            and <= TransactionStoreOptions.MaxCapacity,
                $"{TransactionStoreOptions.SectionName}:{nameof(TransactionStoreOptions.Capacity)} must be between " +
                $"{TransactionStoreOptions.MinCapacity} and {TransactionStoreOptions.MaxCapacity}.");

        services.TryAddSingleton(TimeProvider.System);

        // Singletons: the store is the process-wide window of state, and the in-process bus holds
        // the subscriber list. Both must be shared by every request.
        services.TryAddSingleton<ITransactionStore, InMemoryTransactionStore>();
        services.TryAddSingleton<ITransactionEventBus, InProcessTransactionEventBus>();

        services.TryAddSingleton<TransactionValidator>();
        services.TryAddSingleton<TransactionIngestionService>();

        return services;
    }
}
