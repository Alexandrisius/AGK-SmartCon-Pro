using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Issue #249, Phase 4: change classification (trivial/minor/major) and
/// the version-vs-version diff computation.
/// </summary>
public sealed class ContentChangeClassifierTests
{
    [Theory]
    [InlineData("DEF", ContentChangeClass.Major)]
    [InlineData("GEOM", ContentChangeClass.Major)]
    [InlineData("CONN", ContentChangeClass.Major)]
    [InlineData("STRUCT", ContentChangeClass.Major)]
    [InlineData("ROUTING", ContentChangeClass.Major)]
    [InlineData("TYPES", ContentChangeClass.Minor)]
    [InlineData("VALUES", ContentChangeClass.Minor)]
    [InlineData("PARAMS", ContentChangeClass.Minor)]
    [InlineData("LOOKUP", ContentChangeClass.Minor)]
    [InlineData("NESTEDHASH", ContentChangeClass.Minor)]
    [InlineData("GEOM2D", ContentChangeClass.Trivial)]
    [InlineData("FACTS", ContentChangeClass.Trivial)]
    [InlineData("META", ContentChangeClass.Trivial)]
    public void Classify_SingleSection_MatchesApprovedDefaults(string section, ContentChangeClass expected)
    {
        Assert.Equal(expected, ContentChangeClassifier.Classify(new[] { section }));
    }

    [Fact]
    public void Classify_PerTypeFlattenedKey_ClassifiedByBaseSection()
    {
        // System per-type keys ("STRUCT|Толстая") classify by the base name.
        Assert.Equal(ContentChangeClass.Major,
            ContentChangeClassifier.Classify(new[] { "STRUCT|Толстая" }));
        Assert.Equal(ContentChangeClass.Minor,
            ContentChangeClassifier.Classify(new[] { "VALUES|Тонкая" }));
    }

    [Fact]
    public void Classify_StrongestWins()
    {
        Assert.Equal(ContentChangeClass.Major,
            ContentChangeClassifier.Classify(new[] { "GEOM2D", "TYPES", "CONN" }));
        Assert.Equal(ContentChangeClass.Minor,
            ContentChangeClassifier.Classify(new[] { "GEOM2D", "FLAGS" }));
        Assert.Equal(ContentChangeClass.Trivial,
            ContentChangeClassifier.Classify(new[] { "GEOM2D" }));
        Assert.Equal(ContentChangeClass.None,
            ContentChangeClassifier.Classify(System.Array.Empty<string>()));
    }
}

public sealed class ContentVersionDiffComputerTests
{
    private static ContentSectionHash Section(string name, string hash, string? typeName = null)
        => new(name, name + "-canonical", hash, typeName);

    [Fact]
    public void Compute_SectionHashDiff_DetectsChangedAndOneSided()
    {
        var incoming = new[]
        {
            Section("META", "M1"),
            Section("TYPES", "T1"),
            Section("GEOM", "G1"),
            Section("NEWSEC", "N1"),
        };
        var active = new Dictionary<string, string>
        {
            ["META"] = "M1",
            ["TYPES"] = "T2",       // changed
            ["GEOM"] = "G1",
            ["OLDSEC"] = "O1",      // removed on the incoming side
        };

        var diff = ContentVersionDiffComputer.Compute(incoming, null, active, null);

        Assert.Equal(new[] { "NEWSEC", "OLDSEC", "TYPES" }, diff.ChangedSections);
        Assert.Equal(ContentChangeClass.Minor, diff.Class);
    }

    [Fact]
    public void Compute_PerTypeDiff_ChangedAddedRemoved()
    {
        var incomingTypes = new[]
        {
            FamilyTypeHashEntry.ForLoadableType("Ду50", "A1"),
            FamilyTypeHashEntry.ForLoadableType("Ду80", "B2"),
            FamilyTypeHashEntry.ForLoadableType("Ду100", "C1"),
        };
        var activeTypes = new[]
        {
            FamilyTypeHashEntry.ForLoadableType("Ду50", "A1"),
            FamilyTypeHashEntry.ForLoadableType("Ду80", "B1"),
            FamilyTypeHashEntry.ForLoadableType("Ду65", "D1"),
        };

        var diff = ContentVersionDiffComputer.Compute(
            System.Array.Empty<ContentSectionHash>(), incomingTypes,
            new Dictionary<string, string>(), activeTypes);

        Assert.Equal(new[] { "Ду80" }, diff.ChangedTypes);
        Assert.Equal(new[] { "Ду100" }, diff.AddedTypes);
        Assert.Equal(new[] { "Ду65" }, diff.RemovedTypes);
    }

    [Fact]
    public void Compute_IdenticalContent_NoneClass()
    {
        var incoming = new[] { Section("META", "M1"), Section("TYPES", "T1") };
        var active = new Dictionary<string, string> { ["META"] = "M1", ["TYPES"] = "T1" };

        var diff = ContentVersionDiffComputer.Compute(incoming, null, active, null);

        Assert.Equal(ContentChangeClass.None, diff.Class);
        Assert.Empty(diff.ChangedSections);
    }

    [Fact]
    public void Compute_MissingActiveAnalytics_TreatsEverythingChanged()
    {
        // Active version has no stored sections yet (backfill pending) —
        // every incoming section reads as changed (the window shows the
        // "analytics pending" notice instead of a fake empty diff).
        var incoming = new[] { Section("META", "M1") };

        var diff = ContentVersionDiffComputer.Compute(incoming, null, null, null);

        Assert.Equal(new[] { "META" }, diff.ChangedSections);
    }
}
