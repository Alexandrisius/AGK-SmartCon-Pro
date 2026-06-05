using System.Text.Encodings.Web;
using System.Text.Json;
using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class FamilyMetadataPackageExtensionsTests
{
    [Fact]
    public void WithNonNullCollections_AllPopulated_ReturnsSameInstance()
    {
        var source = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true },
            Categories = [new FamilyMetadataCategoryNode { Name = "A" }],
            Attributes = [new FamilyMetadataAttribute { Name = "X" }],
            Bindings = [new FamilyMetadataBinding { CategoryPath = "A", AttributeName = "X" }]
        };

        var result = source.WithNonNullCollections();

        Assert.Same(source, result);
    }

    [Fact]
    public void WithNonNullCollections_AllCollectionsNull_DefensivelyPopulatesEmpty()
    {
        var source = new FamilyMetadataPackage();

        var result = source.WithNonNullCollections();

        Assert.NotNull(result.Sections);
        Assert.NotNull(result.Categories);
        Assert.NotNull(result.Attributes);
        Assert.NotNull(result.Bindings);
        Assert.Empty(result.Categories);
        Assert.Empty(result.Attributes);
        Assert.Empty(result.Bindings);
        Assert.Equal(source.Format, result.Format);
        Assert.Equal(source.Version, result.Version);
    }

    [Fact]
    public void WithNonNullCollections_OnlyAttributesNull_DefaultsJustThat()
    {
        var source = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true },
            Categories = [new FamilyMetadataCategoryNode { Name = "Pipes" }],
            Attributes = null!,
            Bindings = [new FamilyMetadataBinding { CategoryPath = "Pipes", AttributeName = "X" }]
        };

        var result = source.WithNonNullCollections();

        Assert.NotNull(result.Attributes);
        Assert.Empty(result.Attributes);
        Assert.Same(source.Sections, result.Sections);
        Assert.Same(source.Categories, result.Categories);
        Assert.Same(source.Bindings, result.Bindings);
    }

    [Fact]
    public void WithNonNullCollections_PreservesScalarFields()
    {
        var exportedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var source = new FamilyMetadataPackage
        {
            Format = "smartcon.familymanager.metadata-package",
            Version = 2,
            ExportedAtUtc = exportedAt
        };

        var result = source.WithNonNullCollections();

        Assert.Equal("smartcon.familymanager.metadata-package", result.Format);
        Assert.Equal(2, result.Version);
        Assert.Equal(exportedAt, result.ExportedAtUtc);
    }

    [Fact]
    public void WithNonNullCollections_AfterDeserializingNullCollections_PreventsImportNRE()
    {
        const string jsonWithNullCollections = """
            {
              "format": "smartcon.familymanager.metadata-package",
              "version": 2,
              "exportedAtUtc": "2026-01-15T12:00:00+00:00",
              "sections": { "categories": false, "attributes": true, "bindings": false },
              "categories": null,
              "attributes": null,
              "bindings": null
            }
            """;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        var raw = JsonSerializer.Deserialize<FamilyMetadataPackage>(jsonWithNullCollections, options);
        Assert.NotNull(raw);

        Assert.Null(raw!.Categories);
        Assert.Null(raw.Attributes);
        Assert.Null(raw.Bindings);

        var normalized = raw.WithNonNullCollections();

        Assert.NotNull(normalized.Attributes);
        var enumerator = normalized.Attributes.GetEnumerator();
        Assert.False(enumerator.MoveNext());
    }
}
