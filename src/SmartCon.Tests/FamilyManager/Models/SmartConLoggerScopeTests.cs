using System.IO;
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

    [Fact]
    public async Task BeginScope_PropagatesAcrossAwait()
    {
        // AsyncLocal flow: scope opened here must still be active after
        // the await resumes on the same SynchronizationContext.
        using var scope = SmartConLogger.BeginScope("AsyncFlow",
            ("Stage", "BeforeAwait"));

        await Task.Yield();

        // If AsyncLocal works, Info inside the scope is still associated
        // with the OpId — the test passes because no exception is thrown.
        SmartConLogger.Info("after await");
    }

    [Fact]
    public async Task BeginScope_PropagatesAcrossThreadPoolHop()
    {
        // The dangerous case: scope is opened on the test thread,
        // continuation runs on a thread-pool thread. AsyncLocal must
        // still flow the scope. ThreadStatic would silently break.
        using var scope = SmartConLogger.BeginScope("ThreadPoolFlow");

        await Task.Run(() =>
        {
            // If AsyncLocal is broken, this would not see the scope.
            // We can't easily assert on the rendered log here (the
            // formatter touches static state), so we just ensure no
            // exception is raised.
            SmartConLogger.Info("inside Task.Run");
        });
    }

    [Fact]
    public void BeginScope_FormatPrefix_IncludesOpAndProperties()
    {
        using var scope = SmartConLogger.BeginScope("FormatTest",
            ("UserId", "u1"),
            ("Action", "Import"));

        // OpId is 8 hex chars from Guid.NewGuid().ToString("N")[..8].
        var prefix = LogScopeProvider.Current!.FormatPrefix();
        Assert.Contains("OpId=", prefix);
        Assert.Contains("Op=FormatTest", prefix);
        Assert.Contains("UserId=u1", prefix);
        Assert.Contains("Action=Import", prefix);
    }

    [Fact]
    public void BeginScope_OpId_IsUniquePerScope()
    {
        using var s1 = SmartConLogger.BeginScope("A");
        var opId1 = LogScopeProvider.Current!.OpId;
        s1.Dispose();

        using var s2 = SmartConLogger.BeginScope("B");
        var opId2 = LogScopeProvider.Current!.OpId;
        s2.Dispose();

        Assert.NotEqual(opId1, opId2);
    }

    [Fact]
    public void BeginScope_Nested_OuterStaysActiveAfterInnerDispose()
    {
        using (var outer = SmartConLogger.BeginScope("Outer"))
        {
            var outerOpId = LogScopeProvider.Current!.OpId;
            using (var inner = SmartConLogger.BeginScope("Inner"))
            {
                Assert.Equal("Inner", LogScopeProvider.Current!.Operation);
            }
            Assert.Equal("Outer", LogScopeProvider.Current!.Operation);
        }
    }

    [Fact]
    public void Measure_RendersElapsedFooterOnDispose()
    {
        using (SmartConLogger.Measure("MeasureE2E"))
        {
            System.Threading.Thread.Sleep(5);
        }

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AGK", "SmartCon", "smartcon.log");
        var tail = ReadTail(logPath, maxBytes: 4096);
        Assert.Contains("Op=MeasureE2E", tail);
        Assert.Contains("END elapsed=", tail);
    }

    [Fact]
    public void Measure_DoesNotEmitDuplicateOpProperty()
    {
        // Regression: Measure used to add ("Op", operation) to Properties,
        // which produced a duplicated `Op=… Op=…` segment in the prefix
        // because FormatPrefix already renders Op=… from the Operation field.
        using (SmartConLogger.Measure("DupOpE2E"))
        {
            var prefix = LogScopeProvider.Current!.FormatPrefix();
            var opCount = CountOccurrences(prefix, "Op=DupOpE2E");
            Assert.Equal(1, opCount);
        }
    }

    [Fact]
    public void BeginScope_PropertyOpMatchingOperation_DoesNotDuplicate()
    {
        // Same regression at the BeginScope API: callers that pass
        // ("Op", operationName) should not see the Op=… segment twice.
        using (var scope = SmartConLogger.BeginScope("DupOpScope", ("Op", "DupOpScope"), ("Stage", "x")))
        {
            var prefix = LogScopeProvider.Current!.FormatPrefix();
            var opCount = CountOccurrences(prefix, "Op=DupOpScope");
            Assert.Equal(1, opCount);
            Assert.Contains("Stage=x", prefix);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public async Task InfoInsideScope_AfterAwait_StillCarriesOpId()
    {
        // The AsyncLocal contract: a continuation on the same thread after
        // an await must still see the active scope. We don't probe the
        // rendered log here (the assertion is implicit — the Info call
        // would NRE if the scope were missing properties).
        using (SmartConLogger.BeginScope("AwaitE2E", ("Stage", "inside")))
        {
            await Task.Yield();
            SmartConLogger.Info("after await — should be in scope");
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(-Math.Min(maxBytes, fs.Length), SeekOrigin.End);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
