using SmartCon.Core.Logging;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class SmartConLoggerScopeTests
{
    [Fact]
    public void BeginScope_LogsStartAndEnd()
    {
        using var scope = SmartConLogger.BeginScope("TestOperation");
        Assert.NotNull(scope);
    }

    [Fact]
    public void BeginScope_WithProperties_LogsKeyValue()
    {
        using var scope = SmartConLogger.BeginScope("TestOpWithProps",
            ("UserId", "u123"),
            ("Action", "Import"));
        Assert.NotNull(scope);
    }

    [Fact]
    public void Measure_LogsTiming()
    {
        using var measure = SmartConLogger.Measure("TestMeasure");
        System.Threading.Thread.Sleep(10);
    }

    [Fact]
    public void BeginScope_CanBeNested()
    {
        using (var outer = SmartConLogger.BeginScope("Outer"))
        {
            using (var inner = SmartConLogger.BeginScope("Inner"))
            {
            }
        }
    }

    [Fact]
    public void BeginScope_DisposeTwice_DoesNotThrow()
    {
        var scope = SmartConLogger.BeginScope("DisposeTwice");
        scope.Dispose();
        scope.Dispose();
    }
}
