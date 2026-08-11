using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// FHV8 (#209, ADR-066): composite content hashes over the shared-nested
/// closure. Direct edges are derived from FLAT per-document subtree scans
/// by subtraction; composition is bottom-up, so a deep content change
/// (bolt) transitively shifts every ancestor (flange, valve).
/// </summary>
public class CompositeFamilyHashComposerTests
{
    private readonly FamilyContentHasher _hasher = new();
    private readonly CompositeFamilyHashComposer _composer;

    public CompositeFamilyHashComposerTests()
    {
        _composer = new CompositeFamilyHashComposer(_hasher);
    }

    private static FamilySnapshot Snap(string name, string paramMarker = "P1") =>
        new(
            FamilyName: name,
            Category: "Pipe Fittings",
            Parameters:
            [
                new FamilyParameterInfo(
                    paramMarker, "Double", "Group", false, false, null, false, false, null, null),
            ],
            Types: [],
            Geometry: new GeometryMetrics(0, []),
            SharedNestedFamilyNames: []);

    private static Dictionary<string, FamilySnapshot> MapOf(params FamilySnapshot[] snaps) =>
        snaps.ToDictionary(s => s.FamilyName, s => s, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlyList<string>> Subtrees(
        params (string Owner, string[] Children)[] entries) =>
        entries.ToDictionary(
            e => e.Owner,
            e => (IReadOnlyList<string>)e.Children,
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Compose_NoNested_PlainOwnHash()
    {
        var snapshots = MapOf(Snap("A"));
        var hashes = _composer.Compose(snapshots, Subtrees());

        Assert.Equal(
            _hasher.ComputeForLoadable(Snap("A"))!.HexString,
            hashes["A"]!.HexString);
    }

    [Fact]
    public void Compose_Chain_GrandchildChangeShiftsAllAncestors()
    {
        // Valve → Flange → Bolt (flat subtree scans, probe P1 semantics).
        var snapshots = MapOf(Snap("Valve"), Snap("Flange"), Snap("Bolt"));
        var subtrees = Subtrees(
            ("Valve", new[] { "Flange", "Bolt" }),
            ("Flange", new[] { "Bolt" }));

        var before = _composer.Compose(snapshots, subtrees);

        var snapshotsChanged = MapOf(Snap("Valve"), Snap("Flange"), Snap("Bolt", paramMarker: "P2"));
        var after = _composer.Compose(snapshotsChanged, subtrees);

        Assert.NotEqual(before["Bolt"]!.HexString, after["Bolt"]!.HexString);
        Assert.NotEqual(before["Flange"]!.HexString, after["Flange"]!.HexString);
        Assert.NotEqual(before["Valve"]!.HexString, after["Valve"]!.HexString);
    }

    [Fact]
    public void Compose_Chain_OwnChangeOfIntermediateShiftsOnlyItsAncestors()
    {
        var snapshots = MapOf(Snap("Valve"), Snap("Flange"), Snap("Bolt"));
        var subtrees = Subtrees(
            ("Valve", new[] { "Flange", "Bolt" }),
            ("Flange", new[] { "Bolt" }));

        var before = _composer.Compose(snapshots, subtrees);

        // Flange's OWN content changes, Bolt stays the same.
        var snapshotsChanged = MapOf(Snap("Valve"), Snap("Flange", paramMarker: "P2"), Snap("Bolt"));
        var after = _composer.Compose(snapshotsChanged, subtrees);

        Assert.Equal(before["Bolt"]!.HexString, after["Bolt"]!.HexString);
        Assert.NotEqual(before["Flange"]!.HexString, after["Flange"]!.HexString);
        Assert.NotEqual(before["Valve"]!.HexString, after["Valve"]!.HexString);
    }

    [Fact]
    public void Compose_Diamond_SharedGrandchildReachesRootThroughEitherParent()
    {
        // Root → {B, C}, both B and C embed D. D is also in Root's flat
        // subtree. Direct edges: Root→{B,C} (D subtracted), B→{D}, C→{D}.
        var snapshots = MapOf(Snap("Root"), Snap("B"), Snap("C"), Snap("D"));
        var subtrees = Subtrees(
            ("Root", new[] { "B", "C", "D" }),
            ("B", new[] { "D" }),
            ("C", new[] { "D" }));

        var before = _composer.Compose(snapshots, subtrees);
        var after = _composer.Compose(
            MapOf(Snap("Root"), Snap("B"), Snap("C"), Snap("D", paramMarker: "P2")), subtrees);

        Assert.NotEqual(before["D"]!.HexString, after["D"]!.HexString);
        Assert.NotEqual(before["B"]!.HexString, after["B"]!.HexString);
        Assert.NotEqual(before["C"]!.HexString, after["C"]!.HexString);
        Assert.NotEqual(before["Root"]!.HexString, after["Root"]!.HexString);
    }

    [Fact]
    public void Compose_MissingChildSnapshot_UsesUnreadableMarker_Deterministically()
    {
        // "Ghost" is referenced by the parent's scan but has no extracted
        // snapshot (open failure) — the parent still gets a deterministic
        // hash via the UNREADABLE marker.
        var snapshots = MapOf(Snap("Parent"));
        var subtrees = Subtrees(("Parent", new[] { "Ghost" }));

        var first = _composer.Compose(snapshots, subtrees);
        var second = _composer.Compose(snapshots, subtrees);

        Assert.NotNull(first["Parent"]);
        Assert.Equal(first["Parent"]!.HexString, second["Parent"]!.HexString);
        // And it differs from the no-children plain hash (the marker IS content).
        Assert.NotEqual(
            _hasher.ComputeForLoadable(Snap("Parent"))!.HexString,
            first["Parent"]!.HexString);
    }

    [Fact]
    public void Compose_IsDeterministicAcrossInsertionOrder()
    {
        var subtrees = Subtrees(
            ("Valve", new[] { "Flange", "Bolt" }),
            ("Flange", new[] { "Bolt" }));

        var direct = _composer.Compose(
            MapOf(Snap("Valve"), Snap("Flange"), Snap("Bolt")), subtrees);
        var reversed = _composer.Compose(
            MapOf(Snap("Bolt"), Snap("Flange"), Snap("Valve")), subtrees);

        Assert.Equal(direct["Valve"]!.HexString, reversed["Valve"]!.HexString);
        Assert.Equal(direct["Flange"]!.HexString, reversed["Flange"]!.HexString);
        Assert.Equal(direct["Bolt"]!.HexString, reversed["Bolt"]!.HexString);
    }

    [Fact]
    public void Compose_NestedStandaloneEqualsNestedInClosure()
    {
        // The SAME own snapshot + subtree must produce the same composite
        // hash whether the family is the root of the composition or a
        // nested member of a larger closure — the migration (per-group
        // closure from the managed .rfa) and the import-time batch
        // composition rely on this equivalence.
        var standalone = _composer.Compose(
            MapOf(Snap("Flange"), Snap("Bolt")),
            Subtrees(("Flange", new[] { "Bolt" })));

        var inClosure = _composer.Compose(
            MapOf(Snap("Valve"), Snap("Flange"), Snap("Bolt")),
            Subtrees(
                ("Valve", new[] { "Flange", "Bolt" }),
                ("Flange", new[] { "Bolt" })));

        Assert.Equal(standalone["Flange"]!.HexString, inClosure["Flange"]!.HexString);
        Assert.Equal(standalone["Bolt"]!.HexString, inClosure["Bolt"]!.HexString);
    }

    [Fact]
    public void Compose_CaseInsensitiveNameMatching()
    {
        var snapshots = MapOf(Snap("Parent"), Snap("Child"));
        var subtrees = Subtrees(("Parent", new[] { "CHILD" }));

        var hashes = _composer.Compose(snapshots, subtrees);

        // The case-mismatched scan entry resolves to the extracted child —
        // no UNREADABLE marker — so adding a real child changes the hash
        // relative to the childless parent.
        Assert.NotEqual(
            _hasher.ComputeForLoadable(Snap("Parent"))!.HexString,
            hashes["Parent"]!.HexString);
    }
}
