using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class FamilyLeafNodeViewModelTests
{
    private static readonly IFamilyAssetService AssetService = new Mock<IFamilyAssetService>().Object;

    private static FamilyLeafNodeViewModel CreateLeaf(
        ContentStatus status = ContentStatus.Active,
        int? activeRevit = null,
        int? minRevit = null,
        int currentRevit = 2025)
    {
        var row = new FamilyCatalogItemRow
        {
            Id = "item1",
            Name = "FamA",
            ContentStatus = status,
            ActiveRevitMajorVersion = activeRevit,
            MinRevitMajorVersion = minRevit,
        };
        return new FamilyLeafNodeViewModel(row, AssetService, currentRevitVersion: currentRevit);
    }

    [Fact]
    public void ActiveCompatible_IsAvailable()
    {
        var leaf = CreateLeaf(activeRevit: 2021, minRevit: 2021, currentRevit: 2025);

        Assert.False(leaf.IsDeprecated);
        Assert.False(leaf.IsRevitIncompatible);
        Assert.False(leaf.IsUnavailable);
        Assert.Equal(FamilyUnavailableReason.None, leaf.UnavailableReason);
        Assert.Null(leaf.RequiredRevitVersion);
    }

    [Fact]
    public void Deprecated_IsUnavailableWithDeprecatedReason()
    {
        var leaf = CreateLeaf(status: ContentStatus.Deprecated, activeRevit: 2021, minRevit: 2021);

        Assert.True(leaf.IsDeprecated);
        Assert.False(leaf.IsRevitIncompatible);
        Assert.True(leaf.IsUnavailable);
        Assert.Equal(FamilyUnavailableReason.Deprecated, leaf.UnavailableReason);
    }

    [Fact]
    public void Retired_TreatedAsDeprecated()
    {
        var leaf = CreateLeaf(status: ContentStatus.Retired, activeRevit: 2021, minRevit: 2021);

        Assert.True(leaf.IsDeprecated);
        Assert.True(leaf.IsUnavailable);
        Assert.Equal(FamilyUnavailableReason.Deprecated, leaf.UnavailableReason);
    }

    [Fact]
    public void NewerRevitVersion_IsUnavailableWithRevitReason()
    {
        var leaf = CreateLeaf(activeRevit: 2025, minRevit: 2025, currentRevit: 2024);

        Assert.False(leaf.IsDeprecated);
        Assert.True(leaf.IsRevitIncompatible);
        Assert.True(leaf.IsUnavailable);
        Assert.Equal(FamilyUnavailableReason.RevitVersion, leaf.UnavailableReason);
        Assert.Equal(2025, leaf.RequiredRevitVersion);
    }

    [Fact]
    public void DeprecatedAndNewerRevitVersion_HasBothReason()
    {
        var leaf = CreateLeaf(status: ContentStatus.Deprecated, activeRevit: 2025, minRevit: 2025, currentRevit: 2024);

        Assert.True(leaf.IsUnavailable);
        Assert.Equal(FamilyUnavailableReason.DeprecatedAndRevitVersion, leaf.UnavailableReason);
        Assert.Equal(2025, leaf.RequiredRevitVersion);
    }

    [Fact]
    public void SameRevitVersion_IsCompatible()
    {
        var leaf = CreateLeaf(activeRevit: 2025, minRevit: 2025, currentRevit: 2025);

        Assert.False(leaf.IsRevitIncompatible);
        Assert.False(leaf.IsUnavailable);
    }

    [Fact]
    public void NoVersionInfo_TreatedAsCompatible()
    {
        var leaf = CreateLeaf(activeRevit: null, minRevit: null, currentRevit: 2024);

        Assert.False(leaf.IsRevitIncompatible);
        Assert.False(leaf.IsUnavailable);
        Assert.False(leaf.HasCompatibleVersion);
    }

    [Fact]
    public void UnknownCurrentRevitVersion_TreatedAsCompatible()
    {
        var leaf = CreateLeaf(activeRevit: 2025, minRevit: 2025, currentRevit: 0);

        Assert.False(leaf.IsRevitIncompatible);
        Assert.False(leaf.IsUnavailable);
        Assert.False(leaf.HasCompatibleVersion);
    }

    [Fact]
    public void HasCompatibleVersion_TrueWhenAnyOlderVersionExists()
    {
        var leaf = CreateLeaf(activeRevit: 2025, minRevit: 2021, currentRevit: 2024);

        Assert.True(leaf.IsRevitIncompatible);
        Assert.True(leaf.HasCompatibleVersion);
    }

    [Fact]
    public void HasCompatibleVersion_FalseWhenAllVersionsNewer()
    {
        var leaf = CreateLeaf(activeRevit: 2025, minRevit: 2025, currentRevit: 2024);

        Assert.True(leaf.IsRevitIncompatible);
        Assert.False(leaf.HasCompatibleVersion);
    }
}
