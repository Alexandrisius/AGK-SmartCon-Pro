using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Inline autocomplete pickers of the assignment condition editor (#241):
/// the editable field shows the picked label, typing live-filters the
/// dropdown and invalidates the pick until re-picked, focus resets to the
/// full list.
/// </summary>
public sealed class AssignmentConditionRowPickerTests
{
    private static readonly IReadOnlyList<AssignmentOperatorItem> Ops =
    [
        new(ValidationRuleOperator.Equals, "Равно"),
        new(ValidationRuleOperator.NotEquals, "Не равно"),
    ];

    private static readonly IReadOnlyList<AssignmentValueItem> Categories =
    [
        new("-2008049", "Фитинги трубопроводов"),
        new("-2008010", "Фитинги воздуховодов"),
        new("-2000053", "Трубы"),
    ];

    private static AssignmentConditionRowViewModel NewRow() =>
        new(null, AssignmentConditionSourceKind.System, null, AssignmentSystemField.RevitCategory,
            ValidationRuleOperator.Equals, null, null, null, null, true,
            Ops, Ops, Ops, Categories, []);

    [Fact]
    public void Pick_SetsKeyTextAndClosesPopup()
    {
        var row = NewRow();
        row.IsCategoryPopupOpen = true;

        row.SelectedCategoryItem = Categories[0];

        Assert.Equal("-2008049", row.ValueText);
        Assert.Equal("Фитинги трубопроводов", row.CategoryPickerText);
        Assert.False(row.IsCategoryPopupOpen);
    }

    [Fact]
    public void TypingDivergentText_InvalidatesPickAndOpensPopup()
    {
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];

        row.CategoryPickerText = "трубы";

        Assert.Null(row.ValueText);
        Assert.True(row.IsCategoryPopupOpen);
    }

    [Fact]
    public void Typing_FiltersListByContains()
    {
        var row = NewRow();

        row.CategoryPickerText = "воздух";

        Assert.Single(row.FilteredRevitCategories);
        Assert.Equal("-2008010", row.FilteredRevitCategories[0].Key);
    }

    [Fact]
    public void OpenPicker_ResetsTextToPickedLabelAndShowsFullList()
    {
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];

        // User erased and typed garbage → the pick is invalidated by
        // design (typing diverges from the picked label).
        row.CategoryPickerText = "xyz";
        row.IsCategoryPopupOpen = false;
        Assert.Null(row.ValueText);

        row.OpenCategoryPickerCommand.Execute(null);

        // No valid pick left — the field resets to empty, the full
        // unfiltered list is shown for a fresh pick.
        Assert.Equal(string.Empty, row.CategoryPickerText);
        Assert.Equal(3, row.FilteredRevitCategories.Count);
        Assert.True(row.IsCategoryPopupOpen);
    }

    [Fact]
    public void OpenPicker_WithValidPick_RestoresLabel()
    {
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];
        row.IsCategoryPopupOpen = false;

        row.OpenCategoryPickerCommand.Execute(null);

        // The pick survives a focus-open with unchanged text.
        Assert.Equal("Фитинги трубопроводов", row.CategoryPickerText);
        Assert.Equal("-2008049", row.ValueText);
        Assert.Equal(3, row.FilteredRevitCategories.Count);
    }

    [Fact]
    public void FilterText_EmptyWhenShowingPickedLabel()
    {
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];

        row.OpenCategoryPickerCommand.Execute(null);

        Assert.Equal(string.Empty, row.CategoryFilterText);
    }

    [Fact]
    public void NullItemPick_DoesNotClearStoredValue()
    {
        // The ListBox pushes null when the filtered list loses the current
        // item — must not wipe the stored key.
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];

        row.SelectedCategoryItem = null;

        Assert.Equal("-2008049", row.ValueText);
    }

    [Fact]
    public void LoadedCondition_ShowsPickedLabel()
    {
        var row = new AssignmentConditionRowViewModel(
            "c1", AssignmentConditionSourceKind.System, null, AssignmentSystemField.RevitCategory,
            ValidationRuleOperator.Equals, "-2008049", null, null, null, true,
            Ops, Ops, Ops, Categories, []);

        Assert.Equal("Фитинги трубопроводов", row.CategoryPickerText);
        Assert.Equal("Фитинги трубопроводов", row.SelectedCategoryLabel);
    }

    [Fact]
    public void SystemFieldSwitch_ClearsPickerTextToo()
    {
        // Gate review: a pick + field switch must clear BOTH the stored
        // key and the displayed text — a stale label would show a pick the
        // model no longer holds.
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];

        row.SystemField = AssignmentSystemField.PartType;

        Assert.Null(row.ValueText);
        Assert.Equal(string.Empty, row.CategoryPickerText);
        Assert.False(row.IsCategoryPopupOpen);
    }
}
