using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class ClockTests
{
    [Fact]
    public void SystemClock_UtcNow_IsCloseToRealTime()
    {
        var clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;

        var actual = clock.UtcNow;

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(actual.UtcDateTime, before.UtcDateTime, after.UtcDateTime);
    }

    [Fact]
    public void FakeClock_ReturnsPresetValue()
    {
        var fixedValue = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        IClock clock = new FakeClock(fixedValue);

        Assert.Equal(fixedValue, clock.UtcNow);
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset value) => _value = value;
        private readonly DateTimeOffset _value;
        public DateTimeOffset UtcNow => _value;
    }
}

public sealed class GuidIdGeneratorTests
{
    [Fact]
    public void NewId_DefaultFormat_ReturnsUniqueNonEmpty()
    {
        IIdGenerator gen = new GuidIdGenerator();

        var id1 = gen.NewId();
        var id2 = gen.NewId();

        Assert.False(string.IsNullOrEmpty(id1));
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void NewId_WithFormat_ReturnsFormatted()
    {
        IIdGenerator gen = new GuidIdGenerator();

        var id = gen.NewId("N");

        Assert.Equal(32, id.Length);
    }
}
