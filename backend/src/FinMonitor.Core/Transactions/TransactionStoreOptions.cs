namespace FinMonitor.Core.Transactions;

public sealed class TransactionStoreOptions
{
    public const string SectionName = "TransactionStore";

    public const int MinCapacity = 1;

    public const int MaxCapacity = 100_000;

    /// <summary>
    /// How many transactions to retain. The monitor is a live view, not an archive: once the
    /// window is full the least recently updated transaction is evicted. This bound is what keeps
    /// a long-running pod's memory flat under sustained ingestion.
    /// </summary>
    /// <remarks>
    /// Keep this aligned with the retained length of the distributed event log. The log is what a
    /// restarting replica replays to rebuild this window, so a log shorter than the window would
    /// leave the replica with a permanently thinner view than its peers.
    /// </remarks>
    public int Capacity { get; set; } = 500;
}
