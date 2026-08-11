using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// E2 (#209): post-load ES markers for dependency children — the embedded
/// version label is written, NULL-label links are skipped, per-child
/// failures never fail the load.
/// </summary>
public sealed class NestedDependencyMarkerWriterTests
{
    private readonly Mock<IFamilyDependencyRepository> _dependencyRepository = new();
    private readonly Mock<IFamilyCatalogProvider> _catalog = new();
    private readonly Mock<IFamilyVersionWriter> _versionWriter = new();

    [Fact]
    public async Task NoLinks_WritesNothing()
    {
        _dependencyRepository
            .Setup(r => r.GetForCurrentVersionAsync("parent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FamilyDependencyInfo>());

        var written = await NestedDependencyMarkerWriter.WriteMarkersAsync(
            _dependencyRepository.Object, _catalog.Object, _versionWriter.Object, "parent-1", 2025, CancellationToken.None);

        Assert.Equal(0, written);
        _versionWriter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LinkWithEmbeddedLabel_WritesMarkerWithEmbeddedVersion()
    {
        var links = new[]
        {
            new FamilyDependencyInfo("child-1", FamilyDependencyKind.SharedNested, null, 0, ChildVersionLabel: "v1"),
        };
        _dependencyRepository
            .Setup(r => r.GetForCurrentVersionAsync("parent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);
        _catalog
            .Setup(c => c.GetItemAsync("child-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeItem("child-1", "Фланец"));

        var written = await NestedDependencyMarkerWriter.WriteMarkersAsync(
            _dependencyRepository.Object, _catalog.Object, _versionWriter.Object, "parent-1", 2025, CancellationToken.None);

        Assert.Equal(1, written);
        _versionWriter.Verify(w => w.WriteVersionMarkerAsync(
            "child-1", "Фланец", "v1", 2025, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LinkWithoutEmbeddedLabel_Skipped()
    {
        var links = new[]
        {
            new FamilyDependencyInfo("child-1", FamilyDependencyKind.Routing, "Отвод:Стандарт", 0),
        };
        _dependencyRepository
            .Setup(r => r.GetForCurrentVersionAsync("parent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);

        var written = await NestedDependencyMarkerWriter.WriteMarkersAsync(
            _dependencyRepository.Object, _catalog.Object, _versionWriter.Object, "parent-1", 2025, CancellationToken.None);

        Assert.Equal(0, written);
        _versionWriter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChildMarkerFailure_DoesNotThrow_OtherChildrenStillWritten()
    {
        var links = new[]
        {
            new FamilyDependencyInfo("child-bad", FamilyDependencyKind.SharedNested, null, 0, ChildVersionLabel: "v1"),
            new FamilyDependencyInfo("child-good", FamilyDependencyKind.SharedNested, null, 1, ChildVersionLabel: "v2"),
        };
        _dependencyRepository
            .Setup(r => r.GetForCurrentVersionAsync("parent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(links);
        _catalog
            .Setup(c => c.GetItemAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => MakeItem(id, id));
        _versionWriter
            .Setup(w => w.WriteVersionMarkerAsync("child-bad", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ES store unavailable"));

        var written = await NestedDependencyMarkerWriter.WriteMarkersAsync(
            _dependencyRepository.Object, _catalog.Object, _versionWriter.Object, "parent-1", 2025, CancellationToken.None);

        Assert.Equal(1, written);
        _versionWriter.Verify(w => w.WriteVersionMarkerAsync(
            "child-good", "child-good", "v2", 2025, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static FamilyCatalogItem MakeItem(string id, string name) =>
        new(
            Id: id,
            Name: name,
            NormalizedName: name.ToUpperInvariant(),
            Description: null,
            CategoryPath: null,
            CategoryId: null,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: "v2",
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            RevitCategoryId: null);
}
