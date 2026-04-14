using Moq;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class MappingRuleItemTests
{
    private static MappingRuleItem MakeItem(
        IDialogService? dialogService = null,
        IReadOnlyList<string>? families = null)
        => new(dialogService ?? new Mock<IDialogService>().Object, families ?? []);

    // ── From ──────────────────────────────────────────────────────────────

    [Fact]
    public void From_CopiesTypeCodes()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(2),
        };
        var item = MappingRuleItem.From(rule, new Mock<IDialogService>().Object, []);
        Assert.Equal(1, item.FromTypeCode);
        Assert.Equal(2, item.ToTypeCode);
    }

    [Fact]
    public void From_CopiesIsDirectConnect()
    {
        var item = MappingRuleItem.From(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(1),
                IsDirectConnect = true,
            },
            new Mock<IDialogService>().Object, []);
        Assert.True(item.IsDirectConnect);
    }

    [Fact]
    public void From_SingleFamily_AddedToCollection()
    {
        var item = MappingRuleItem.From(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                FittingFamilies = [new FittingMapping { FamilyName = "Переходник", Priority = 1 }],
            },
            new Mock<IDialogService>().Object, []);
        Assert.Single(item.FittingFamilies);
        Assert.Equal("Переходник", item.FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void From_MultipleFamilies_PreservesOrderByPriority()
    {
        var item = MappingRuleItem.From(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Б", Priority = 2 },
                    new FittingMapping { FamilyName = "А", Priority = 1 },
                ],
            },
            new Mock<IDialogService>().Object, []);
        Assert.Equal(2, item.FittingFamilies.Count);
        Assert.Equal("А", item.FittingFamilies[0].FamilyName);
        Assert.Equal("Б", item.FittingFamilies[1].FamilyName);
    }

    [Fact]
    public void From_NoFamilies_EmptyCollection()
    {
        var item = MappingRuleItem.From(
            new FittingMappingRule
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
            },
            new Mock<IDialogService>().Object, []);
        Assert.Empty(item.FittingFamilies);
    }

    // ── ToRule ────────────────────────────────────────────────────────────

    [Fact]
    public void ToRule_RoundTrip_TypeCodes()
    {
        var item = MakeItem();
        item.FromTypeCode = 3;
        item.ToTypeCode = 5;
        var rule = item.ToRule();
        Assert.Equal(3, rule.FromType.Value);
        Assert.Equal(5, rule.ToType.Value);
    }

    [Fact]
    public void ToRule_RoundTrip_IsDirectConnect()
    {
        var item = MakeItem();
        item.IsDirectConnect = true;
        Assert.True(item.ToRule().IsDirectConnect);
    }

    [Fact]
    public void ToRule_SingleFamily_OneFitting()
    {
        var item = MakeItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Переходник", Priority = 1 });
        var rule = item.ToRule();
        Assert.Single(rule.FittingFamilies);
        Assert.Equal("Переходник", rule.FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void ToRule_MultipleFamilies_PreservesCollection()
    {
        var item = MakeItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "А", Priority = 1 });
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Б", Priority = 2 });
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "В", Priority = 3 });
        var rule = item.ToRule();
        Assert.Equal(3, rule.FittingFamilies.Count);
        Assert.Equal("А", rule.FittingFamilies[0].FamilyName);
        Assert.Equal("В", rule.FittingFamilies[2].FamilyName);
    }

    [Fact]
    public void ToRule_EmptyFamilies_NoFittings()
    {
        var item = MakeItem();
        Assert.Empty(item.ToRule().FittingFamilies);
    }

    // ── FamiliesSummary ───────────────────────────────────────────────────

    [Fact]
    public void FamiliesSummary_NoFamilies_ShowsPlaceholder()
    {
        var item = MakeItem();
        Assert.Equal("(не выбраны)", item.FamiliesSummary);
    }

    [Fact]
    public void FamiliesSummary_OneFamiliy_ShowsName()
    {
        var item = MakeItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Угольник", Priority = 1 });
        Assert.Equal("Угольник", item.FamiliesSummary);
    }

    [Fact]
    public void FamiliesSummary_MultipleFamilies_JoinedWithComma()
    {
        var item = MakeItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "А", Priority = 1 });
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Б", Priority = 2 });
        Assert.Equal("А, Б", item.FamiliesSummary);
    }

    // ── PropertyChanged ───────────────────────────────────────────────────

    [Fact]
    public void FamiliesSummary_PropertyChangedRaised_WhenFamilyAdded()
    {
        var item = MakeItem();
        var changed = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.FamiliesSummary)) changed = true;
        };
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Новое", Priority = 1 });
        Assert.True(changed);
    }

    [Fact]
    public void FamiliesSummary_PropertyChangedRaised_WhenFamilyRemoved()
    {
        var item = MakeItem();
        var f = new FittingMapping { FamilyName = "X", Priority = 1 };
        item.FittingFamilies.Add(f);
        var changed = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.FamiliesSummary)) changed = true;
        };
        item.FittingFamilies.Remove(f);
        Assert.True(changed);
    }

    // ── OpenFamilySelector command ────────────────────────────────────────

    [Fact]
    public void OpenFamilySelectorCommand_OnConfirm_UpdatesFittingFamilies()
    {
        var newFamilies = new List<FittingMapping>
        {
            new() { FamilyName = "Муфта", Priority = 1 },
        };
        var dialogMock = new Mock<IDialogService>();
        dialogMock
            .Setup(d => d.ShowFamilySelector(It.IsAny<IReadOnlyList<string>>(),
                                             It.IsAny<IReadOnlyList<FittingMapping>>()))
            .Returns(newFamilies);

        var item = MakeItem(dialogMock.Object, ["Муфта"]);
        item.OpenFamilySelectorCommand.Execute(null);

        Assert.Single(item.FittingFamilies);
        Assert.Equal("Муфта", item.FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void OpenFamilySelectorCommand_OnCancel_DoesNotChangeFamilies()
    {
        var original = new FittingMapping { FamilyName = "Угольник", Priority = 1 };
        var dialogMock = new Mock<IDialogService>();
        dialogMock
            .Setup(d => d.ShowFamilySelector(It.IsAny<IReadOnlyList<string>>(),
                                             It.IsAny<IReadOnlyList<FittingMapping>>()))
            .Returns((IReadOnlyList<FittingMapping>?)null);

        var item = MakeItem(dialogMock.Object);
        item.FittingFamilies.Add(original);
        item.OpenFamilySelectorCommand.Execute(null);

        Assert.Single(item.FittingFamilies);
        Assert.Equal("Угольник", item.FittingFamilies[0].FamilyName);
    }
}
