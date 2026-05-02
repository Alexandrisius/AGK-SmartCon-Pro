using System.Collections.ObjectModel;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class CategoryTreeEditorStaticTests
{
    private static CategoryNode MakeNode(string id, string name, string? parentId = null, int sortOrder = 0) =>
        new(id, name, parentId, sortOrder, parentId is null ? name : $"{parentId}/{name}", DateTimeOffset.UtcNow);

    [Fact]
    public void FlattenImportData_SingleRoot_ReturnsOneNode()
    {
        var items = new List<CategoryTreeImportData.CategoryImportItem>
        {
            new() { Name = "Pipes" },
        };

        var result = CategoryTreeEditorViewModel.FlattenImportData(items, null, 0);

        Assert.Single(result);
        Assert.Equal("Pipes", result[0].Name);
        Assert.Null(result[0].ParentId);
    }

    [Fact]
    public void FlattenImportData_NestedChildren_SetsParentId()
    {
        var items = new List<CategoryTreeImportData.CategoryImportItem>
        {
            new()
            {
                Name = "Pipes",
                Children =
                [
                    new() { Name = "Steel" },
                    new() { Name = "Copper" },
                ]
            },
        };

        var result = CategoryTreeEditorViewModel.FlattenImportData(items, null, 0);

        Assert.Equal(3, result.Count);
        Assert.Null(result[0].ParentId);
        Assert.NotNull(result[0].Id);
        Assert.Equal(result[0].Id, result[1].ParentId);
        Assert.Equal(result[0].Id, result[2].ParentId);
    }

    [Fact]
    public void FlattenImportData_DeepNesting_SetsCorrectPath()
    {
        var items = new List<CategoryTreeImportData.CategoryImportItem>
        {
            new()
            {
                Name = "A",
                Children =
                [
                    new()
                    {
                        Name = "B",
                        Children = [new() { Name = "C" }]
                    }
                ]
            },
        };

        var result = CategoryTreeEditorViewModel.FlattenImportData(items, null, 0);

        Assert.Equal(3, result.Count);
        Assert.Null(result[0].ParentId);
        Assert.Equal(result[0].Id, result[1].ParentId);
        Assert.Equal(result[1].Id, result[2].ParentId);
    }

    [Fact]
    public void FlattenImportData_EmptyList_ReturnsEmpty()
    {
        var result = CategoryTreeEditorViewModel.FlattenImportData([], null, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void FlattenImportData_SetsSortOrder()
    {
        var items = new List<CategoryTreeImportData.CategoryImportItem>
        {
            new() { Name = "First" },
            new() { Name = "Second" },
            new() { Name = "Third" },
        };

        var result = CategoryTreeEditorViewModel.FlattenImportData(items, null, 5);

        Assert.Equal(5, result[0].SortOrder);
        Assert.Equal(6, result[1].SortOrder);
        Assert.Equal(7, result[2].SortOrder);
    }

    [Fact]
    public void BuildExportTree_SingleRoot_ReturnsSingleItem()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "Pipes"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryTreeEditorViewModel.BuildExportTree(tree, null);

        Assert.Single(result);
        Assert.Equal("Pipes", result[0].Name);
    }

    [Fact]
    public void BuildExportTree_NestedStructure_ReturnsHierarchy()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "Pipes"),
            MakeNode("2", "Steel", "1"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryTreeEditorViewModel.BuildExportTree(tree, null);

        Assert.Single(result);
        Assert.Equal("Pipes", result[0].Name);
        Assert.NotNull(result[0].Children);
        Assert.Single(result[0].Children!);
        Assert.Equal("Steel", result[0].Children![0].Name);
    }

    [Fact]
    public void ComputeRecursiveCount_WithChildren_SumsAll()
    {
        var counts = new Dictionary<string, int>
        {
            ["1"] = 5,
            ["2"] = 3,
            ["3"] = 2,
        };

        var root = new CategoryNodeViewModel(MakeNode("1", "Root"));
        var child1 = new CategoryNodeViewModel(MakeNode("2", "Child1"));
        var child2 = new CategoryNodeViewModel(MakeNode("3", "Child2"));
        root.Children.Add(child1);
        root.Children.Add(child2);

        var result = CategoryTreeEditorViewModel.ComputeRecursiveCount(root, counts);

        Assert.Equal(10, result);
    }

    [Fact]
    public void ComputeRecursiveCount_NoChildren_ReturnsOwnCount()
    {
        var counts = new Dictionary<string, int> { ["1"] = 7 };
        var node = new CategoryNodeViewModel(MakeNode("1", "Solo"));

        var result = CategoryTreeEditorViewModel.ComputeRecursiveCount(node, counts);

        Assert.Equal(7, result);
    }

    [Fact]
    public void ComputeRecursiveCount_NotInCounts_ReturnsZero()
    {
        var counts = new Dictionary<string, int>();
        var node = new CategoryNodeViewModel(MakeNode("1", "Solo"));

        var result = CategoryTreeEditorViewModel.ComputeRecursiveCount(node, counts);

        Assert.Equal(0, result);
    }

    [Fact]
    public void TryRemoveNode_ExistingId_RemovesAndReturnsTrue()
    {
        var nodes = new ObservableCollection<CatalogTreeNodeViewModel>
        {
            new CategoryNodeViewModel(MakeNode("1", "A")),
            new CategoryNodeViewModel(MakeNode("2", "B")),
        };

        var result = CategoryTreeEditorViewModel.TryRemoveNode(nodes, "1");

        Assert.True(result);
        Assert.Single(nodes);
    }

    [Fact]
    public void TryRemoveNode_NonExistentId_ReturnsFalse()
    {
        var nodes = new ObservableCollection<CatalogTreeNodeViewModel>
        {
            new CategoryNodeViewModel(MakeNode("1", "A")),
        };

        var result = CategoryTreeEditorViewModel.TryRemoveNode(nodes, "99");

        Assert.False(result);
        Assert.Single(nodes);
    }

    [Fact]
    public void TryRemoveNode_NestedId_RemovesFromChildren()
    {
        var root = new CategoryNodeViewModel(MakeNode("1", "Root"));
        var child = new CategoryNodeViewModel(MakeNode("2", "Child"));
        root.Children.Add(child);

        var nodes = new ObservableCollection<CatalogTreeNodeViewModel> { root };

        var result = CategoryTreeEditorViewModel.TryRemoveNode(nodes, "2");

        Assert.True(result);
        Assert.Empty(root.Children);
    }
}
