using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// #221 follow-up: the unified type-node gates. The presence dot, the
/// context menu and the DnD start must never disagree — they share the
/// pure predicates <see cref="FamilyManagerMainViewModel.CanLoadLeafToProject"/>
/// and <see cref="FamilyManagerMainViewModel.CanUpdateTypeNode"/>.
/// </summary>
public sealed class TypeNodeGuardTests
{
    [Fact]
    public void CanLoadLeafToProject_AllConditionsTrue_Allowed()
    {
        Assert.True(FamilyManagerMainViewModel.CanLoadLeafToProject(
            ContentStatus.Active,
            isRevitIncompatible: false,
            canLoadToProject: true,
            activeBaseCompatibleWithCurrentDoc: true));
    }

    [Theory]
    [InlineData(ContentStatus.Deprecated, false, true, true)]
    [InlineData(ContentStatus.Active, true, true, true)]
    [InlineData(ContentStatus.Active, false, false, true)]
    [InlineData(ContentStatus.Active, false, true, false)]
    public void CanLoadLeafToProject_AnyConditionFalse_Denied(
        ContentStatus status, bool revitIncompatible, bool canLoad, bool baseCompatible)
    {
        Assert.False(FamilyManagerMainViewModel.CanLoadLeafToProject(
            status, revitIncompatible, canLoad, baseCompatible));
    }

    [Fact]
    public void CanUpdateTypeNode_NotInProjectAndNotStale_Denied()
    {
        // A grey dot (not in project) never offers «Обновить».
        Assert.False(FamilyManagerMainViewModel.CanUpdateTypeNode(
            isInProject: false,
            isStaleInProject: false,
            ContentStatus.Active,
            isRevitIncompatible: false,
            canLoadToProject: true,
            activeBaseCompatibleWithCurrentDoc: true));
    }

    [Theory]
    [InlineData(true, false)] // in project, current
    [InlineData(false, true)] // stale in project
    [InlineData(true, true)]
    public void CanUpdateTypeNode_InProjectOrStale_Allowed(bool isInProject, bool isStale)
    {
        Assert.True(FamilyManagerMainViewModel.CanUpdateTypeNode(
            isInProject,
            isStale,
            ContentStatus.Active,
            isRevitIncompatible: false,
            canLoadToProject: true,
            activeBaseCompatibleWithCurrentDoc: true));
    }

    [Fact]
    public void CanUpdateTypeNode_StaleButLeafGateFails_Denied()
    {
        Assert.False(FamilyManagerMainViewModel.CanUpdateTypeNode(
            isInProject: false,
            isStaleInProject: true,
            ContentStatus.Deprecated,
            isRevitIncompatible: false,
            canLoadToProject: true,
            activeBaseCompatibleWithCurrentDoc: true));
    }

    [Fact]
    public void CanUpdateTypeNode_HasNoIsVirtualParameter_ByDesign()
    {
        // #221: the predicate deliberately takes no IsVirtual — for a
        // typeless family (virtual <default> node) the update is
        // family-scoped and must be offered exactly like for real types.
        // This test pins the signature: adding an IsVirtual parameter back
        // breaks compilation here, not just behavior.
        var parameters = typeof(FamilyManagerMainViewModel)
            .GetMethod(
                nameof(FamilyManagerMainViewModel.CanUpdateTypeNode),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetParameters();

        Assert.DoesNotContain(parameters, p => p.Name?.Contains("Virtual", StringComparison.OrdinalIgnoreCase) == true);
    }
}
