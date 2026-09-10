using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// #187: tri-state presence dot on the type node — grey (not in project),
/// blue (present + up to date), orange (present + stale).
/// </summary>
public sealed class FamilyTypeNodePresenceTests
{
    [Fact]
    public void PresenceState_NotInProject_WhenBothFlagsFalse()
    {
        var node = CreateNode();

        Assert.Equal(TypePresenceState.NotInProject, node.PresenceState);
    }

    [Fact]
    public void PresenceState_InProject_WhenPresentAndNotStale()
    {
        var node = CreateNode();
        node.IsInProject = true;

        Assert.Equal(TypePresenceState.InProject, node.PresenceState);
    }

    [Fact]
    public void PresenceState_StaleInProject_WhenPresentAndStale()
    {
        var node = CreateNode();
        node.IsInProject = true;
        node.IsStaleInProject = true;

        Assert.Equal(TypePresenceState.StaleInProject, node.PresenceState);
    }

    [Fact]
    public void PresenceState_StaleFlagIgnored_WhenNotInProject()
    {
        var node = CreateNode();
        node.IsStaleInProject = true;

        Assert.Equal(TypePresenceState.NotInProject, node.PresenceState);
    }

    [Fact]
    public void PresenceState_RaisesPropertyChanged_OnFlagChanges()
    {
        var node = CreateNode();
        var raised = new List<string?>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        node.IsInProject = true;
        node.IsStaleInProject = true;

        Assert.Equal(2, raised.Count(n => n == nameof(FamilyTypeNodeViewModel.PresenceState)));
    }

    private static FamilyTypeNodeViewModel CreateNode() =>
        new("item1", "Стандарт", familySource: "system", familyName: "Conduit with Fittings");
}
