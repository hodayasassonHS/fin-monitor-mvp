namespace FinMonitor.Core.Tests.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> pinned to an instant the test controls.
/// </summary>
/// <remarks>
/// Production code takes <see cref="TimeProvider"/> rather than reading
/// <see cref="DateTimeOffset.UtcNow"/> precisely so that clock-dependent rules — the future
/// timestamp guard, for instance — can be tested deterministically instead of with a sleep.
/// </remarks>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
