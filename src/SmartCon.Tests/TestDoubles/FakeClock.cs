using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Deterministic clock for unit tests. Default constructor seeds
/// <see cref="UtcNow"/> to a fixed instant so tests do not depend on
/// wall-clock time. The value constructor is for explicit deterministic
/// timestamps. The setter is for time-mutation tests (rare).
/// </summary>
public sealed class FakeClock : IClock
{
    public FakeClock()
    {
        UtcNow = new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero);
    }

    public FakeClock(DateTimeOffset value)
    {
        UtcNow = value;
    }

    public DateTimeOffset UtcNow { get; set; }
}
