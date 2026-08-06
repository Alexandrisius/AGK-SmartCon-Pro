using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Import;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// ADR-066 (E1): dependency link planning for the Phase-3 batch executor —
/// imported-child detection by ORIGINAL dialog path (staging rewrites
/// FilePath to the managed .rfa path), dedup-link for skipped-but-existing
/// children, no links for skipped/failed parents.
/// </summary>
public sealed class DependencyLinkPlannerTests
{
    private const string ParentPath = "system://OST_PipeCurves";
    private const string ParentItemId = "parent-1";

    private static readonly IReadOnlyDictionary<string, string> ImportedParents =
        new Dictionary<string, string> { [ParentPath] = ParentItemId };

    private static FamilyBatchImportItem MakeChild(
        string fileName,
        FamilyBatchImportAction action,
        string? precomputedId = null,
        string? existingId = null,
        string? parentPath = ParentPath) =>
        new(
            FilePath: $"loadable://{fileName}",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: existingId is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
            ExistingCatalogItemId: existingId,
            FamilySource: "loadable",
            PrecomputedCatalogItemId: precomputedId,
            DependencyLinks:
            [
                new FamilyDependencyLink(parentPath!, FamilyDependencyKind.Routing, $"{fileName}:Стандарт"),
            ])
        {
            Action = action,
        };

    [Fact]
    public void ImportedChild_LinksPlannedWithPrecomputedId()
    {
        var child = MakeChild("Отвод", FamilyBatchImportAction.IncrementVersion, precomputedId: "child-new-1");

        var plan = DependencyLinkPlanner.Build(
            [child], ImportedParents, ["loadable://Отвод"]);

        var links = Assert.Single(plan.LinksByParent);
        Assert.Equal(ParentItemId, links.Key);
        var link = Assert.Single(links.Value);
        Assert.Equal("child-new-1", link.ChildCatalogItemId);
        Assert.Equal(FamilyDependencyKind.Routing, link.Kind);
        Assert.Equal("Отвод:Стандарт", link.PartName);
        Assert.Empty(plan.UnresolvedChildren);
        Assert.Empty(plan.LinksWithParentNotImported);
    }

    [Fact]
    public void SkippedExistingChild_DedupLinkOnExistingId()
    {
        var child = MakeChild("Отвод", FamilyBatchImportAction.Skip, precomputedId: "existing-1", existingId: "existing-1");

        var plan = DependencyLinkPlanner.Build(
            [child], ImportedParents, []);

        var links = Assert.Single(plan.LinksByParent);
        Assert.Equal("existing-1", Assert.Single(links.Value).ChildCatalogItemId);
        Assert.Empty(plan.UnresolvedChildren);
    }

    [Fact]
    public void SkippedNewChild_NoLink_Unresolved()
    {
        // New family skipped by the user: not in the catalog, nothing to link.
        var child = MakeChild("Отвод", FamilyBatchImportAction.Skip, precomputedId: "precomputed-not-imported");

        var plan = DependencyLinkPlanner.Build(
            [child], ImportedParents, []);

        Assert.Empty(plan.LinksByParent);
        Assert.Single(plan.UnresolvedChildren);
    }

    [Fact]
    public void FailedChild_NoLink_Unresolved()
    {
        // Neither imported (absent from the original-paths set) nor skipped.
        var child = MakeChild("Отвод", FamilyBatchImportAction.IncrementVersion, precomputedId: "child-err");

        var plan = DependencyLinkPlanner.Build(
            [child], ImportedParents, []);

        Assert.Empty(plan.LinksByParent);
        Assert.Single(plan.UnresolvedChildren);
    }

    [Fact]
    public void ParentNotImported_LinkReported_NotPlanned()
    {
        var child = MakeChild("Отвод", FamilyBatchImportAction.IncrementVersion, precomputedId: "child-1");
        var noParents = new Dictionary<string, string>();

        var plan = DependencyLinkPlanner.Build(
            [child], noParents, ["loadable://Отвод"]);

        Assert.Empty(plan.LinksByParent);
        var (childName, parentPath) = Assert.Single(plan.LinksWithParentNotImported);
        Assert.Equal("Отвод", childName);
        Assert.Equal(ParentPath, parentPath);
    }

    [Fact]
    public void MultipleChildrenSameParent_AggregatedWithOrdinals()
    {
        var childA = MakeChild("Отвод", FamilyBatchImportAction.IncrementVersion, precomputedId: "child-a");
        var childB = MakeChild("Тройник", FamilyBatchImportAction.Skip, existingId: "child-b-existing");

        var plan = DependencyLinkPlanner.Build(
            [childA, childB], ImportedParents, ["loadable://Отвод"]);

        var links = Assert.Single(plan.LinksByParent).Value;
        Assert.Equal(2, links.Count);
        Assert.Equal("child-a", links[0].ChildCatalogItemId);
        Assert.Equal(0, links[0].Ordinal);
        Assert.Equal("child-b-existing", links[1].ChildCatalogItemId);
        Assert.Equal(1, links[1].Ordinal);
    }

    [Fact]
    public void TwoParents_LinksGroupedPerParent()
    {
        const string secondParentPath = "system://OST_DuctCurves";
        var parents = new Dictionary<string, string>
        {
            [ParentPath] = ParentItemId,
            [secondParentPath] = "parent-2",
        };
        var childA = MakeChild("Отвод", FamilyBatchImportAction.IncrementVersion, precomputedId: "child-a");
        var childB = MakeChild("Решётка", FamilyBatchImportAction.IncrementVersion, precomputedId: "child-b", parentPath: secondParentPath);

        var plan = DependencyLinkPlanner.Build(
            [childA, childB], parents, ["loadable://Отвод", "loadable://Решётка"]);

        Assert.Equal(2, plan.LinksByParent.Count);
        Assert.Equal("child-a", Assert.Single(plan.LinksByParent[ParentItemId]).ChildCatalogItemId);
        Assert.Equal("child-b", Assert.Single(plan.LinksByParent["parent-2"]).ChildCatalogItemId);
    }

    [Fact]
    public void NoDependencyRows_EmptyPlan()
    {
        var topLevel = new FamilyBatchImportItem(
            FilePath: "loadable://Standalone",
            FileName: "Standalone",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            FamilySource: "loadable");

        var plan = DependencyLinkPlanner.Build([topLevel], ImportedParents, []);

        Assert.Empty(plan.LinksByParent);
        Assert.Empty(plan.UnresolvedChildren);
        Assert.Empty(plan.LinksWithParentNotImported);
    }
}
