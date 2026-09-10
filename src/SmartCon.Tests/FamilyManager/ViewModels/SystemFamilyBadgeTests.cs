using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Issue #203: family-kind badge on the tree type nodes. The badge is a
/// pure display mapping of the persisted <c>family_types.family_key</c>
/// (ADR-064) — same-named types of different system families must become
/// visually distinguishable, while loadable/single-family/legacy rows
/// must stay badge-less.
/// </summary>
public sealed class SystemFamilyBadgeTests
{
    // ── Mapper: every discriminating token maps to its badge ──────────

    [Theory]
    [InlineData(SystemFamilyKeys.DuctRound, SystemFamilyBadge.ShapeRound)]
    [InlineData(SystemFamilyKeys.FlexDuctRound, SystemFamilyBadge.ShapeRound)]
    [InlineData(SystemFamilyKeys.DuctRectangular, SystemFamilyBadge.ShapeRectangular)]
    [InlineData(SystemFamilyKeys.FlexDuctRectangular, SystemFamilyBadge.ShapeRectangular)]
    [InlineData(SystemFamilyKeys.DuctOval, SystemFamilyBadge.ShapeOval)]
    [InlineData(SystemFamilyKeys.ConduitWithFittings, SystemFamilyBadge.Fittings)]
    [InlineData(SystemFamilyKeys.CableTrayWithFittings, SystemFamilyBadge.Fittings)]
    [InlineData(SystemFamilyKeys.ConduitWithoutFittings, SystemFamilyBadge.FittingsNone)]
    [InlineData(SystemFamilyKeys.CableTrayWithoutFittings, SystemFamilyBadge.FittingsNone)]
    [InlineData(SystemFamilyKeys.WallBasic, SystemFamilyBadge.WallBasic)]
    [InlineData(SystemFamilyKeys.WallStacked, SystemFamilyBadge.WallStacked)]
    [InlineData(SystemFamilyKeys.WallCurtain, SystemFamilyBadge.WallCurtain)]
    [InlineData(SystemFamilyKeys.StairsAssembled, SystemFamilyBadge.StairsAssembled)]
    [InlineData(SystemFamilyKeys.StairsCastInPlace, SystemFamilyBadge.StairsCastInPlace)]
    [InlineData(SystemFamilyKeys.StairsPrecast, SystemFamilyBadge.StairsPrecast)]
    public void Resolve_DiscriminatingToken_MapsToBadge(string token, SystemFamilyBadge expected)
    {
        Assert.Equal(expected, SystemFamilyBadgeMap.Resolve(token));
    }

    // ── Mapper: silent fallback — nothing the tree should not badge ────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(SystemFamilyKeys.SingleFamily)]
    [InlineData(SystemFamilyKeys.WallUnknown)]
    [InlineData(SystemFamilyKeys.StairsUnknown)]
    [InlineData(SystemFamilyKeys.DuctUnknown)]
    [InlineData(SystemFamilyKeys.FlexDuctUnknown)]
    [InlineData("Pipe.FutureDiscriminator")]
    public void Resolve_UndiscriminatingKey_IsNone(string? token)
    {
        Assert.Equal(SystemFamilyBadge.None, SystemFamilyBadgeMap.Resolve(token));
    }

    // ── VM: Badge / BadgeTooltip on the type node ──────────────────────

    [Fact]
    public void Badge_ResolvedFromFamilyKey()
    {
        var node = new FamilyTypeNodeViewModel(
            "item1", "Короб", familySource: "system",
            familyName: "Короб с соединительными деталями",
            familyKey: SystemFamilyKeys.CableTrayWithFittings);

        Assert.Equal(SystemFamilyBadge.Fittings, node.Badge);
        Assert.True(node.HasBadge);
    }

    [Fact]
    public void Badge_LoadableType_IsNone()
    {
        var node = new FamilyTypeNodeViewModel("item1", "TypeA");

        Assert.Equal(SystemFamilyBadge.None, node.Badge);
        Assert.False(node.HasBadge);
        Assert.Null(node.BadgeTooltip);
    }

    [Fact]
    public void BadgeTooltip_IsFamilyName_Alone()
    {
        // #264: the tooltip is the family name and nothing else — the badge
        // points at the family within the category, the kind wording is
        // already part of the name.
        var node = new FamilyTypeNodeViewModel(
            "item1", "Короб", familySource: "system",
            familyName: "Короб без соединительных деталей",
            familyKey: SystemFamilyKeys.CableTrayWithoutFittings);

        Assert.Equal("Короб без соединительных деталей", node.BadgeTooltip);
    }

    [Fact]
    public void BadgeTooltip_FamilyNameWithoutKindWording_Alone()
    {
        // #264: no label prefix even when the name does not state the kind —
        // the category comes from the tree, not the tooltip.
        var node = new FamilyTypeNodeViewModel(
            "item1", "Магистраль", familySource: "system",
            familyName: "Воздуховоды 1",
            familyKey: SystemFamilyKeys.DuctRound);

        Assert.Equal("Воздуховоды 1", node.BadgeTooltip);
    }

    [Fact]
    public void BadgeTooltip_WithoutFamilyName_IsLabelOnly()
    {
        var node = new FamilyTypeNodeViewModel(
            "item1", "Магистраль", familySource: "system",
            familyKey: SystemFamilyKeys.DuctRound);

        Assert.NotNull(node.BadgeTooltip);
        var tooltip = node.BadgeTooltip;

        // Language-independent: the RU or EN label, without a family suffix.
        Assert.Contains(tooltip, new[] { "Круглое сечение", "Round profile" });
    }

    [Fact]
    public void BadgeTooltip_DistinguishesSameNamedSiblings()
    {
        // The #203 repro: two types named «Короб» from the two tray families.
        var withFittings = new FamilyTypeNodeViewModel(
            "item1", "Короб", familySource: "system",
            familyName: "Короб с соединительными деталями",
            familyKey: SystemFamilyKeys.CableTrayWithFittings);
        var withoutFittings = new FamilyTypeNodeViewModel(
            "item1", "Короб", familySource: "system",
            familyName: "Короб без соединительных деталей",
            familyKey: SystemFamilyKeys.CableTrayWithoutFittings);

        Assert.NotEqual(withFittings.Badge, withoutFittings.Badge);
        Assert.NotEqual(withFittings.BadgeTooltip, withoutFittings.BadgeTooltip);
    }
}
