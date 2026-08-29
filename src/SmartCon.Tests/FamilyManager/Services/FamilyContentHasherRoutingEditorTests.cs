using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// ADR-072 Phase 3: byte-exactness proof of
/// <see cref="FamilyContentHasher.RebuildSystemSectionsWithRouting"/> —
/// the routing editor recomputes hashes from stored section strings, and
/// the result must equal what the real snapshot-based hasher path produces
/// for the same content (otherwise reimport dedup and stale detection
/// break silently).
/// </summary>
public class FamilyContentHasherRoutingEditorTests
{
    private readonly FamilyContentHasher _hasher = new();

    private static RoutingPreferencesSnapshot ManagerRouting() => new(
        PreferredJunctionType: 0,
        Rules:
        [
            new RoutingRuleSnapshot(0, "DN50 Сталь", "сегмент", []),
            new RoutingRuleSnapshot(1, "BP_A0301_KAN: BP_A0301", "отвод 90",
            [
                new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.05, 0.2),
            ]),
            new RoutingRuleSnapshot(2, null, "Нет", []),
        ]);

    private static RoutingPreferencesSnapshot ParamRouting() => new(
        PreferredJunctionType: 1,
        Rules:
        [
            new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, "FlexTee: Std", "",
                [], GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")),
            new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, null, "",
                [], GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_UNION_PARAM")),
        ]);

    private static SystemFamilySnapshot CreateSnapshot(RoutingPreferencesSnapshot? firstTypeRouting) => new(
        CategoryName: "Трубы",
        CategoryId: -2008044,
        Types:
        [
            new SystemTypeSnapshot(
                "DN50",
                [new SystemParameterValue("Diameter", "Double", true, "50", 50.0, null)],
                Routing: firstTypeRouting,
                FamilyName: "Pipe Types",
                FamilyKey: "pipe"),
            new SystemTypeSnapshot(
                "Flex 20",
                [new SystemParameterValue("Diameter", "Double", true, "20", 20.0, null)],
                Routing: ParamRouting(),
                FamilyName: "Pipe Types",
                FamilyKey: "flexpipe"),
        ]);

    private static Dictionary<string, string> ToSectionMap(IReadOnlyList<ContentSectionHash> sections)
        => sections.ToDictionary(s => s.Key, s => s.CanonicalString, StringComparer.Ordinal);

    private static IReadOnlyList<RecomposeTypeIdentity> IdentitiesOf(SystemFamilySnapshot snapshot)
        => snapshot.Types
            .Select(t => new RecomposeTypeIdentity(t.Name, t.FamilyKey, t.FamilyName))
            .ToList();

    [Fact]
    public void Rebuild_NoEdits_ReproducesOriginalHashesByteExact()
    {
        var snapshot = CreateSnapshot(ManagerRouting());
        var sections = _hasher.ComputeSectionsForSystem(snapshot)!;
        var originalHash = _hasher.ComputeForSystem(snapshot)!;
        var originalTypeHashes = _hasher.ComputePerTypeHashesForSystem(snapshot)!;

        var result = FamilyContentHasher.RebuildSystemSectionsWithRouting(
            ToSectionMap(sections), IdentitiesOf(snapshot),
            new Dictionary<string, RoutingPreferencesSnapshot?>());

        Assert.Equal(originalHash.HexString, result.ContentHashHex);
        Assert.Equal(
            originalTypeHashes.Select(t => t.HashHex),
            result.TypeHashes.Select(t => t.HashHex));
        Assert.Equal(sections.Count, result.Sections.Count);
        Assert.Equal(
            sections.Select(s => s.CanonicalString),
            result.Sections.Select(s => s.CanonicalString));
    }

    [Fact]
    public void Rebuild_EditedRouting_MatchesSnapshotHasherByteExact()
    {
        var original = CreateSnapshot(ManagerRouting());
        var sections = _hasher.ComputeSectionsForSystem(original)!;

        var editedRouting = new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(0, "DN50 Сталь", "сегмент", []),
            new RoutingRuleSnapshot(1, "OT_Elbow_90: ДУ50", "новый отвод",
            [
                new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.0, 0.0),
            ]),
        ]);
        var editedSnapshot = CreateSnapshot(editedRouting);

        var truthHash = _hasher.ComputeForSystem(editedSnapshot)!;
        var truthTypeHashes = _hasher.ComputePerTypeHashesForSystem(editedSnapshot)!;

        var result = FamilyContentHasher.RebuildSystemSectionsWithRouting(
            ToSectionMap(sections), IdentitiesOf(original),
            new Dictionary<string, RoutingPreferencesSnapshot?> { ["DN50"] = editedRouting });

        Assert.Equal(truthHash.HexString, result.ContentHashHex);
        Assert.Equal(
            truthTypeHashes.Select(t => t.HashHex),
            result.TypeHashes.Select(t => t.HashHex));
        // The untouched type keeps its original hash.
        Assert.Equal(
            _hasher.ComputePerTypeHashesForSystem(original)![1].HashHex,
            result.TypeHashes[1].HashHex);
    }

    [Fact]
    public void Rebuild_RemovedRouting_ProducesCanonicalEmptySection()
    {
        var original = CreateSnapshot(ManagerRouting());
        var sections = _hasher.ComputeSectionsForSystem(original)!;

        var editedSnapshot = CreateSnapshot(firstTypeRouting: null);
        var truthHash = _hasher.ComputeForSystem(editedSnapshot)!;

        var result = FamilyContentHasher.RebuildSystemSectionsWithRouting(
            ToSectionMap(sections), IdentitiesOf(original),
            new Dictionary<string, RoutingPreferencesSnapshot?> { ["DN50"] = null });

        Assert.Equal("ROUTING|-|", FamilyContentHasher.FormatSystemRoutingSection(null));
        Assert.Equal(truthHash.HexString, result.ContentHashHex);
    }

    [Fact]
    public void Rebuild_ParamRouting_RoundtripsThroughFormatterAndParser()
    {
        // The ROUTING section the editor writes must be readable by the
        // Phase-2b parser (backfill path) — same canonical format.
        var routing = ParamRouting();
        var canonical = FamilyContentHasher.FormatSystemRoutingSection(routing);

        var parsed = RoutingSectionParser.Parse(
            new Dictionary<string, string> { ["ROUTING|Flex 20"] = canonical });

        var type = Assert.Single(parsed);
        Assert.Equal("Flex 20", type.TypeName);
        Assert.Equal(routing.PreferredJunctionType, type.Routing.PreferredJunctionType);
        Assert.Equal(routing.Rules.Count, type.Routing.Rules.Count);
        Assert.Equal(
            routing.Rules.Select(r => r.PartName),
            type.Routing.Rules.Select(r => r.PartName));
        Assert.Equal(
            routing.Rules.Select(r => r.GroupKey),
            type.Routing.Rules.Select(r => r.GroupKey));
    }

    [Fact]
    public void Rebuild_MissingMeta_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FamilyContentHasher.RebuildSystemSectionsWithRouting(
                new Dictionary<string, string>(),
                [new RecomposeTypeIdentity("DN50", "pipe", "Pipe Types")],
                new Dictionary<string, RoutingPreferencesSnapshot?>()));
    }

    [Fact]
    public void Rebuild_MissingTypeSection_Throws()
    {
        var snapshot = CreateSnapshot(ManagerRouting());
        var map = ToSectionMap(_hasher.ComputeSectionsForSystem(snapshot)!);
        map.Remove("SEGMENTS|DN50");

        Assert.Throws<InvalidOperationException>(() =>
            FamilyContentHasher.RebuildSystemSectionsWithRouting(
                map, IdentitiesOf(snapshot), new Dictionary<string, RoutingPreferencesSnapshot?>()));
    }
}
