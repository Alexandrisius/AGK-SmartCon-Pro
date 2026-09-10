using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Import;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// #209: marker-first version resolution for nested-child import rows
/// (the verified ES marker outranks identity-hash matching, which cannot
/// see past parameter groups).
/// </summary>
public sealed class EmbeddedMarkerMatchResolverTests
{
    [Fact]
    public void ResolveOverride_NoMarker_ReturnsNull()
    {
        var result = EmbeddedMarkerMatchResolver.ResolveOverride(null, null, "item-1", "v1");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveOverride_EmptyMarkerLabel_ReturnsNull()
    {
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-1", string.Empty, "item-1", "v1");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveOverride_MarkerPointsAtDifferentItem_ReturnsNull()
    {
        // Cross-name / renamed item: the hash path stays the arbiter.
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-2", "v2", "item-1", "v1");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveOverride_MarkerAgreesWithHashMatch_ReturnsNull()
    {
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-1", "v2", "item-1", "v2");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveOverride_MarkerNewerThanHashMatch_Overrides()
    {
        // The core case: identity hash still matches the OLD version (v1)
        // because the merge never propagated the parameter group, but the
        // verified marker says the embedded content is v2.
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-1", "v2", "item-1", "v1");
        Assert.NotNull(result);
        Assert.Equal(FamilyBatchImportStatus.Duplicate, result!.Value.Status);
        Assert.Equal("v2", result.Value.MatchedVersionLabel);
    }

    [Fact]
    public void ResolveOverride_MarkerNewerThanNoHashMatch_Overrides()
    {
        // Flange-0104 case from the 2026-08-11 manual test: content landed
        // minus the group — the identity hash matches NO stored version
        // (dedup says Existing), the marker resolves the version.
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-1", "v2", "item-1", null);
        Assert.NotNull(result);
        Assert.Equal(FamilyBatchImportStatus.Duplicate, result!.Value.Status);
        Assert.Equal("v2", result.Value.MatchedVersionLabel);
    }

    [Fact]
    public void ResolveOverride_MarkerOlderThanCurrent_StillOverrides()
    {
        // The marker may legitimately point at an archived version (the
        // embedded copy was verified against an older label) — the row must
        // then show that archived label so the outdated badge/block works.
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-1", "v1", "item-1", null);
        Assert.NotNull(result);
        Assert.Equal("v1", result!.Value.MatchedVersionLabel);
    }

    [Fact]
    public void ResolveOverride_DedupFoundNoItem_ReturnsNull()
    {
        // Status=New (no name match in the catalog): a marker pointing at
        // some item cannot be reconciled with this row — hash path stays.
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-1", "v2", null, null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveOverride_LabelDiffersOnlyByCase_ReturnsNull()
    {
        var result = EmbeddedMarkerMatchResolver.ResolveOverride("item-1", "V2", "item-1", "v2");
        Assert.Null(result);
    }
}
