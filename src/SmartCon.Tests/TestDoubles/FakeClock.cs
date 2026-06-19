using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Deterministic clock for unit tests. <see cref="UtcNow"/> is a settable
/// property so tests can assert on the exact timestamp written into the
/// stale snapshot. Default is a fixed instant (Unix epoch) so tests do not
/// depend on wall-clock time.
/// </summary>
public sealed class FakeClock : IClock
{
    public FakeClock()
    {
        UtcNow = new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; set; }
}
