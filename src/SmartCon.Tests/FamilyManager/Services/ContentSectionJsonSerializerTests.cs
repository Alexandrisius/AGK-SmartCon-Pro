using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Issue #249, Phase 4: JSON persistence of the canonical content
/// sections (section_hashes / section_strings columns).
/// </summary>
public sealed class ContentSectionJsonSerializerTests
{
    private static IReadOnlyList<ContentSectionHash> Sections() => new[]
    {
        new ContentSectionHash("META", "FHV12|LOADABLE|-1|", "AAAA"),
        new ContentSectionHash("PARAMS", "PARAMS|x|", "BBBB"),
        new ContentSectionHash("STRUCT", "STRUCT|-|", "CCCC", "Толстая|стена"),
        new ContentSectionHash("VALUES", "Тонкая|", "DDDD", "Тонкая"),
    };

    [Fact]
    public void Hashes_RoundTrip()
    {
        var json = ContentSectionJsonSerializer.SerializeHashes(Sections());
        var map = ContentSectionJsonSerializer.Deserialize(json);

        Assert.NotNull(map);
        Assert.Equal("AAAA", map!["META"]);
        Assert.Equal("BBBB", map["PARAMS"]);
        // Per-type entries flatten to "SECTION|{TypeName}".
        Assert.Equal("CCCC", map["STRUCT|Толстая|стена"]);
        Assert.Equal("DDDD", map["VALUES|Тонкая"]);
        Assert.Equal(4, map.Count);
    }

    [Fact]
    public void Strings_RoundTrip_PreservesCanonicalContent()
    {
        var json = ContentSectionJsonSerializer.SerializeStrings(Sections());
        var map = ContentSectionJsonSerializer.Deserialize(json);

        Assert.NotNull(map);
        Assert.Equal("FHV12|LOADABLE|-1|", map!["META"]);
        Assert.Equal("STRUCT|-|", map["STRUCT|Толстая|стена"]);
    }

    [Fact]
    public void Deserialize_MalformedOrEmpty_ReturnsNull()
    {
        Assert.Null(ContentSectionJsonSerializer.Deserialize(null));
        Assert.Null(ContentSectionJsonSerializer.Deserialize(""));
        Assert.Null(ContentSectionJsonSerializer.Deserialize("   "));
        Assert.Null(ContentSectionJsonSerializer.Deserialize("{not json"));
    }

    [Fact]
    public void Serialize_CanonicalOrderPreserved()
    {
        var json = ContentSectionJsonSerializer.SerializeHashes(Sections());
        // Sections appear in their canonical (input) order — diffs and
        // logs read in the same order as the canonical string.
        Assert.True(json.IndexOf("META", StringComparison.Ordinal)
            < json.IndexOf("PARAMS", StringComparison.Ordinal));
    }
}
