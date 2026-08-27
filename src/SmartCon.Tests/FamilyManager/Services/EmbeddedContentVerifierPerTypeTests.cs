using SmartCon.FamilyManager.Services.Stale;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Pure per-type drift-map logic of
/// <see cref="EmbeddedContentVerifier.ComputePerTypeStale"/> (#249,
/// Phase 2): the project rule compares only the intersection (the
/// type-set rule — locally-added types are NOT drift), the
/// family-document rule compares the union with one-sided types stale.
/// </summary>
public sealed class EmbeddedContentVerifierPerTypeTests
{
    private static IReadOnlyDictionary<string, string> Hashes(params (string Name, string Hash)[] types)
        => types.ToDictionary(t => t.Name, t => t.Hash, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ProjectRule_ComparesIntersectionOnly()
    {
        var embedded = Hashes(("Ду50", "A1"), ("Ду80", "B1"), ("LocalOnly", "C1"));
        var file = Hashes(("Ду50", "A1"), ("Ду80", "B2"), ("FileOnly", "D1"));

        var map = EmbeddedContentVerifier.ComputePerTypeStale(embedded, file, fullSet: false);

        // Intersection compared: Ду50 matches, Ду80 drifted.
        Assert.False(map["Ду50"]);
        Assert.True(map["Ду80"]);
        // One-sided types are NOT in the map (type-set rule).
        Assert.DoesNotContain("LocalOnly", map.Keys);
        Assert.DoesNotContain("FileOnly", map.Keys);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void FamilyDocumentRule_OneSidedTypesAreStale()
    {
        var embedded = Hashes(("A", "1"), ("RemovedInFile", "2"));
        var file = Hashes(("A", "1"), ("AddedInFile", "3"));

        var map = EmbeddedContentVerifier.ComputePerTypeStale(embedded, file, fullSet: true);

        Assert.False(map["A"]);
        Assert.True(map["RemovedInFile"]);
        Assert.True(map["AddedInFile"]);
        Assert.Equal(3, map.Count);
    }

    [Fact]
    public void MatchingContent_AllFalseMap()
    {
        var embedded = Hashes(("Ду50", "A1"), ("Ду80", "B1"));
        var file = Hashes(("Ду50", "A1"), ("Ду80", "B1"));

        var map = EmbeddedContentVerifier.ComputePerTypeStale(embedded, file, fullSet: false);

        Assert.Equal(2, map.Count);
        Assert.All(map.Values, stale => Assert.False(stale));
    }

    [Fact]
    public void Lookup_IsCaseInsensitive_DisplayNamesPreserved()
    {
        var embedded = Hashes(("Ду50", "A1"));
        var file = Hashes(("ДУ50", "A2"));

        var map = EmbeddedContentVerifier.ComputePerTypeStale(embedded, file, fullSet: false);

        // Original (display) casing preserved as the key…
        Assert.True(map["Ду50"]);
        // …and lookups by any case form hit the same entry.
        Assert.True(map["ДУ50"]);
        Assert.True(map["ду50"]);
    }
}
