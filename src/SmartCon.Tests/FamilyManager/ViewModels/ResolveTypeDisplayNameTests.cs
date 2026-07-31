using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// #187: ResolveTypeDisplayName — system types with the same name but
/// different families (#183) must be distinguishable in the tree via the
/// "(family)" suffix; loadable and legacy rows keep the plain name.
/// </summary>
public sealed class ResolveTypeDisplayNameTests
{
    [Fact]
    public void SystemType_WithFamilyName_AppendsFamilySuffix()
    {
        var leaf = CreateLeaf(familySource: "system");
        var type = new FamilyTypeDescriptor("t1", "item1", "Стандарт", 0, FamilyName: "Conduit without Fittings");

        var result = FamilyManagerMainViewModel.ResolveTypeDisplayName(type, leaf);

        Assert.Equal("Стандарт (Conduit without Fittings)", result);
    }

    [Fact]
    public void SystemType_NullFamilyName_KeepsPlainName()
    {
        var leaf = CreateLeaf(familySource: "system");
        var type = new FamilyTypeDescriptor("t1", "item1", "Стандарт", 0);

        var result = FamilyManagerMainViewModel.ResolveTypeDisplayName(type, leaf);

        Assert.Equal("Стандарт", result);
    }

    [Fact]
    public void LoadableType_WithFamilyName_KeepsPlainName()
    {
        // Loadable types belong to the single family of the leaf — a suffix
        // would only duplicate the parent's display name.
        var leaf = CreateLeaf(familySource: "loadable");
        var type = new FamilyTypeDescriptor("t1", "item1", "DN50", 0, FamilyName: "Отвод");

        var result = FamilyManagerMainViewModel.ResolveTypeDisplayName(type, leaf);

        Assert.Equal("DN50", result);
    }

    private static FamilyLeafNodeViewModel CreateLeaf(string familySource)
    {
        var row = new FamilyCatalogItemRow
        {
            Id = "item1",
            Name = "LeafDisplay",
            FamilySource = familySource,
        };
        return new FamilyLeafNodeViewModel(row, new Mock<IFamilyAssetService>().Object);
    }
}
