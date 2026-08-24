using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Editable autocomplete pickers of the assignment condition editor (#241)
/// — the VM side: the editable field shows the picked label, typing
/// diverging from it invalidates the pick until re-picked, the highlight
/// filter text is empty while the field shows the picked label. The
/// dropdown filtering itself lives in the AutoCompleteComboBox control
/// (view-level), not in the VM.
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
    public void Pick_SetsKeyAndText()
    {
        var row = NewRow();

        row.SelectedCategoryItem = Categories[0];

        Assert.Equal("-2008049", row.ValueText);
        Assert.Equal("Фитинги трубопроводов", row.CategoryPickerText);
    }

    [Fact]
    public void TypingDivergentText_InvalidatesPick()
    {
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];

        row.CategoryPickerText = "трубы";

        Assert.Null(row.ValueText);
    }

    [Fact]
    public void FilterText_FollowsTypedText_EmptyWhenShowingPickedLabel()
    {
        var row = NewRow();
        row.SelectedCategoryItem = Categories[0];

        // The field shows the picked label — no highlight noise.
        Assert.Equal(string.Empty, row.CategoryFilterText);

        row.CategoryPickerText = "воздух";

        Assert.Equal("воздух", row.CategoryFilterText);
    }

    [Fact]
    public void NullItemPick_DoesNotClearStoredValue()
    {
        // The dropdown pushes null when the filtered list loses the current
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
    }

    [Fact]
    public void FullLists_AreExposedForTheDropdown()
    {
        // The AutoCompleteComboBox filters its own view — the VM exposes
        // the full, unfiltered lists.
        var row = NewRow();

        Assert.Equal(3, row.RevitCategories.Count);
        Assert.Same(Categories, row.RevitCategories);
    }
}
