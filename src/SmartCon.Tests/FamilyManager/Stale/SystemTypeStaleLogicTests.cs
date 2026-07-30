using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Stale;

/// <summary>
/// Unit tests for <see cref="SystemTypeStaleLogic"/> (Issue #104): pure
/// aggregation of per-type ES markers into one item-level stale verdict.
/// </summary>
public sealed class SystemTypeStaleLogicTests
{
    private static FamilyVersion Marker(
        string label = "v2",
        string catalogItemId = "item-1",
        int sourceRevit = 2025)
    {
        return new FamilyVersion(1, catalogItemId, label, DateTimeOffset.MinValue, sourceRevit);
    }

    [Fact]
    public void Aggregate_EmptyMarkers_NotStale()
    {
        var (isStale, reason, loadedLabel) = SystemTypeStaleLogic.Aggregate(
            [], "item-1", "v2", 2025);

        Assert.False(isStale);
        Assert.Equal(StaleReason.None, reason);
        Assert.Null(loadedLabel);
    }

    [Fact]
    public void Aggregate_AllMarkersCurrent_NotStale()
    {
        var (isStale, reason, loadedLabel) = SystemTypeStaleLogic.Aggregate(
            [Marker(), Marker(), Marker()], "item-1", "v2", 2025);

        Assert.False(isStale);
        Assert.Equal(StaleReason.None, reason);
        Assert.Equal("v2", loadedLabel);
    }

    [Fact]
    public void Aggregate_OneTypeWithoutMarker_StaleNoEntityStorage()
    {
        var (isStale, reason, _) = SystemTypeStaleLogic.Aggregate(
            [Marker(), null, Marker()], "item-1", "v2", 2025);

        Assert.True(isStale);
        Assert.Equal(StaleReason.NoEntityStorage, reason);
    }

    [Fact]
    public void Aggregate_OneTypeOutdatedVersion_StaleVersionMismatchWithItsLabel()
    {
        var (isStale, reason, loadedLabel) = SystemTypeStaleLogic.Aggregate(
            [Marker(), Marker(label: "v1")], "item-1", "v2", 2025);

        Assert.True(isStale);
        Assert.Equal(StaleReason.VersionMismatch, reason);
        Assert.Equal("v1", loadedLabel);
    }

    [Fact]
    public void Aggregate_MarkerFromOtherRevit_StaleRevitVersionMismatch()
    {
        var (isStale, reason, _) = SystemTypeStaleLogic.Aggregate(
            [Marker(sourceRevit: 2024)], "item-1", "v2", 2025);

        Assert.True(isStale);
        Assert.Equal(StaleReason.RevitVersionMismatch, reason);
    }

    [Fact]
    public void Aggregate_ForeignCatalogItemMarker_StaleVersionMismatch()
    {
        var (isStale, reason, _) = SystemTypeStaleLogic.Aggregate(
            [Marker(catalogItemId: "other-item")], "item-1", "v2", 2025);

        Assert.True(isStale);
        Assert.Equal(StaleReason.VersionMismatch, reason);
    }

    [Fact]
    public void ComputeReason_UnknownTargetRevit_SkipsRevitMismatch()
    {
        var reason = SystemTypeStaleLogic.ComputeReason(
            Marker(sourceRevit: 2024), "item-1", "v2", targetRevit: 0);

        Assert.Equal(StaleReason.None, reason);
    }

    [Fact]
    public void ComputeReason_EmptyCurrentLabel_SkipsVersionMismatch()
    {
        var reason = SystemTypeStaleLogic.ComputeReason(
            Marker(label: "v9"), "item-1", currentVersionLabel: null, targetRevit: 2025);

        Assert.Equal(StaleReason.None, reason);
    }
}
