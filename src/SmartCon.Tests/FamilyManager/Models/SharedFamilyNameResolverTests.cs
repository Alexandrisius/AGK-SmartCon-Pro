using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// Unit tests for <see cref="SharedFamilyNameResolver"/> — the pure-C# helper
/// that picks the best available name for a shared nested family conflict
/// (Revit API value > catalog DB fallback > generic placeholder).
/// Extracted from <c>RevitFamilyLoadOptions</c> so the counter + fallback
/// logic is testable in isolation. The Revit API types involved in
/// <c>IFamilyLoadOptions.OnSharedFamilyFound</c> are sealed native types
/// (see skill <c>smartcon-testing</c>, <c>revit-mocking.md</c>).
/// </summary>
public sealed class SharedFamilyNameResolverTests
{
    [Fact]
    public void Resolve_NoNestedList_NoRevitName_ReturnsPlaceholder()
    {
        var resolver = new SharedFamilyNameResolver();

        var (name, source) = resolver.Resolve(null, invocationIndex: 1);

        Assert.Equal(SharedFamilyNameSource.FallbackPlaceholder, source);
        Assert.Equal("<shared nested #1>", name);
    }

    [Fact]
    public void Resolve_NoNestedList_WhitespaceRevitName_ReturnsPlaceholder()
    {
        var resolver = new SharedFamilyNameResolver();

        var (name, source) = resolver.Resolve("   ", invocationIndex: 2);

        Assert.Equal(SharedFamilyNameSource.FallbackPlaceholder, source);
        Assert.Equal("<shared nested #2>", name);
    }

    [Fact]
    public void Resolve_RevitNamePresent_UsesRevitName()
    {
        var resolver = new SharedFamilyNameResolver(
            new[] { "Болт М12", "Гайка М12" });

        var (name, source) = resolver.Resolve("FromRevit", invocationIndex: 1);

        Assert.Equal(SharedFamilyNameSource.RevitApi, source);
        Assert.Equal("FromRevit", name);
    }

    [Fact]
    public void Resolve_RevitNameNull_UsesCatalogDbByCounter()
    {
        var resolver = new SharedFamilyNameResolver(
            new[] { "Болт М12", "Гайка М12", "Шайба М12" });

        var (first, src1) = resolver.Resolve(null, invocationIndex: 1);
        var (second, src2) = resolver.Resolve(null, invocationIndex: 2);
        var (third, src3) = resolver.Resolve(null, invocationIndex: 3);

        Assert.Equal(SharedFamilyNameSource.CatalogDb, src1);
        Assert.Equal("Болт М12", first);
        Assert.Equal(SharedFamilyNameSource.CatalogDb, src2);
        Assert.Equal("Гайка М12", second);
        Assert.Equal(SharedFamilyNameSource.CatalogDb, src3);
        Assert.Equal("Шайба М12", third);
    }

    [Fact]
    public void Resolve_CounterExceedsList_ReturnsPlaceholder()
    {
        var resolver = new SharedFamilyNameResolver(
            new[] { "Болт М12" });

        var (name, source) = resolver.Resolve(null, invocationIndex: 5);

        Assert.Equal(SharedFamilyNameSource.FallbackPlaceholder, source);
        Assert.Equal("<shared nested #5>", name);
    }

    [Fact]
    public void Resolve_CounterZero_ReturnsPlaceholder()
    {
        var resolver = new SharedFamilyNameResolver(
            new[] { "Болт М12" });

        var (name, source) = resolver.Resolve(null, invocationIndex: 0);

        // Counter must be 1-based; 0 falls through to placeholder.
        Assert.Equal(SharedFamilyNameSource.FallbackPlaceholder, source);
    }

    [Fact]
    public void NextInvocation_IncrementsAcrossCalls()
    {
        var resolver = new SharedFamilyNameResolver();

        Assert.Equal(1, resolver.NextInvocationIndex());
        Assert.Equal(2, resolver.NextInvocationIndex());
        Assert.Equal(3, resolver.NextInvocationIndex());
    }

    [Fact]
    public void NextInvocation_IsThreadSafe()
    {
        var resolver = new SharedFamilyNameResolver();
        var counter = 0;
        var max = 1000;

        Parallel.For(0, max, _ =>
        {
            var i = resolver.NextInvocationIndex();
            Interlocked.Increment(ref counter);
            // Last value seen should be <= max
            Assert.True(i <= max, $"Invocation {i} exceeded max {max}");
        });

        Assert.Equal(max, counter);
    }

    [Fact]
    public void TotalInBatch_ReturnsListCount()
    {
        var withList = new SharedFamilyNameResolver(new[] { "a", "b", "c" });
        Assert.Equal(3, withList.TotalInBatch);

        var empty = new SharedFamilyNameResolver();
        Assert.Equal(0, empty.TotalInBatch);

        var nullCtor = new SharedFamilyNameResolver(null);
        Assert.Equal(0, nullCtor.TotalInBatch);
    }

    [Fact]
    public void Constructor_DoesNotMutateInputList()
    {
        var input = new List<string> { "Болт М12" };
        var resolver = new SharedFamilyNameResolver(input);

        resolver.Resolve(null, 1);

        Assert.Single(input);
        Assert.Equal("Болт М12", input[0]);
    }

    [Fact]
    public void Resolve_RevitApiPreferredOverCatalogDb_ForSameInvocation()
    {
        var resolver = new SharedFamilyNameResolver(
            new[] { "Болт М12" });

        var (name, source) = resolver.Resolve("FromRevit", invocationIndex: 1);

        Assert.Equal(SharedFamilyNameSource.RevitApi, source);
        Assert.Equal("FromRevit", name);
    }
}
