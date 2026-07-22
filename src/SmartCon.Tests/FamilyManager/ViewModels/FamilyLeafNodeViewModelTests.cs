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

    private static FamilyLeafNodeViewModel CreateLeafWithTags(
        string name, string[] tags, string? searchText)
    {
        var row = new FamilyCatalogItemRow
        {
            Id = "item1",
            Name = name,
            Tags = tags,
        };
        return new FamilyLeafNodeViewModel(row, AssetService, searchText: searchText);
    }

    [Fact]
    public void MatchedTags_Empty_WhenNoSearchText()
    {
        var leaf = CreateLeafWithTags("FamA", ["steel"], null);

        Assert.Empty(leaf.MatchedTags);
        Assert.False(leaf.HasMatchedTags);
    }

    [Fact]
    public void MatchedTags_Empty_WhenTokenMatchesNameOnly()
    {
        var leaf = CreateLeafWithTags("Steel Pipe", ["steel"], "steel");

        Assert.Empty(leaf.MatchedTags);
        Assert.False(leaf.HasMatchedTags);
    }

    [Fact]
    public void MatchedTags_ReturnsTag_WhenTokenMatchesTagOnly()
    {
        var leaf = CreateLeafWithTags("Pipe A", ["stainless steel"], "steel");

        Assert.Equal(["stainless steel"], leaf.MatchedTags);
        Assert.True(leaf.HasMatchedTags);
    }

    [Fact]
    public void MatchedTags_OnlyNameUnmatchedTokens_Considered()
    {
        var leaf = CreateLeafWithTags("Pipe A", ["pipe", "stainless steel"], "pipe steel");

        Assert.Equal(["stainless steel"], leaf.MatchedTags);
    }

    [Fact]
    public void MatchedTags_CaseInsensitive()
    {
        var leaf = CreateLeafWithTags("Pipe A", ["Stainless STEEL"], "steel");

        Assert.Equal(["Stainless STEEL"], leaf.MatchedTags);
    }

    [Fact]
    public void MatchedTags_CappedAtThree()
    {
        var leaf = CreateLeafWithTags("Pipe A", ["steel-1", "steel-2", "steel-3", "steel-4"], "steel");

        Assert.Equal(3, leaf.MatchedTags.Count);
    }
}
