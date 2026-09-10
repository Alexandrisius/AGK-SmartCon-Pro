using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.FamilyManager.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="DependencyGuardText"/> (E5, #213, ADR-067). The
/// formatter reads the localized "active" mark through the process-wide
/// <see cref="LocalizationService"/> — serialized via the "Localization"
/// collection with the language pinned (see #214).
/// </summary>
[Collection("Localization")]
public sealed class DependencyGuardTextTests : IDisposable
{
    private readonly Language _originalLanguage;

    public DependencyGuardTextTests()
    {
        _originalLanguage = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.RU);
    }

    public void Dispose() => LocalizationService.SetLanguage(_originalLanguage);

    [Fact]
    public void FormatReferenceLines_GroupsVersionsOfOneParent()
    {
        var lines = DependencyGuardText.FormatReferenceLines(new[]
        {
            new FamilyDependencyReference("p1", "Трубы ГОСТ", "v2", IsCurrentVersion: true),
            new FamilyDependencyReference("p1", "Трубы ГОСТ", "v1", IsCurrentVersion: false),
        });

        var line = Assert.Single(lines);
        Assert.Equal("«Трубы ГОСТ» (v1, v2 (активная))", line);
    }

    [Fact]
    public void FormatReferenceLines_MultipleParents_OrderedByName()
    {
        var lines = DependencyGuardText.FormatReferenceLines(new[]
        {
            new FamilyDependencyReference("p2", "Якорь", "v1", IsCurrentVersion: true),
            new FamilyDependencyReference("p1", "Воздуховоды", "v1", IsCurrentVersion: false),
        });

        Assert.Equal(2, lines.Count);
        Assert.Equal("«Воздуховоды» (v1)", lines[0]);
        Assert.Equal("«Якорь» (v1 (активная))", lines[1]);
    }

    [Fact]
    public void FormatReferenceLines_Empty_ReturnsEmpty()
    {
        Assert.Empty(DependencyGuardText.FormatReferenceLines(
            Array.Empty<FamilyDependencyReference>()));
    }

    [Fact]
    public void FormatReferenceLines_WithoutCurrentMark_PlainVersions()
    {
        // Paperclip tooltip (owner request): «Семейство» (v2) — и всё.
        var lines = DependencyGuardText.FormatReferenceLines(new[]
        {
            new FamilyDependencyReference("p1", "Трубы", "v2", IsCurrentVersion: true),
        }, includeCurrentMark: false);

        Assert.Equal("«Трубы» (v2)", Assert.Single(lines));
    }
}
