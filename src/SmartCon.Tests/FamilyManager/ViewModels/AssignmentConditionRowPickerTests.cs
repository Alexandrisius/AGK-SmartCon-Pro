using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Category/PartType value pickers of the assignment condition editor
/// (#241) — plain ComboBox with SelectedValuePath=Key bound straight to
/// <see cref="AssignmentConditionRowViewModel.ValueText"/>; the built-in
/// TextSearch gives type-to-jump search, no custom controls.
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
    public void LoadedCondition_KeepsKeyInValueText()
    {
        var row = new AssignmentConditionRowViewModel(
            "c1", AssignmentConditionSourceKind.System, null, AssignmentSystemField.RevitCategory,
            ValidationRuleOperator.Equals, "-2008049", null, null, null, true,
            Ops, Ops, Ops, Categories, []);

        Assert.Equal("-2008049", row.ValueText);
    }

    [Fact]
    public void SystemFieldSwitch_ClearsPickedValue()
    {
        // A pick + field switch must clear the stored key — a stale key
        // would re-select an item the user never chose for the new field.
        var row = NewRow();
        row.ValueText = "-2008049";

        row.SystemField = AssignmentSystemField.PartType;

        Assert.Null(row.ValueText);
    }

    [Fact]
    public void Lists_AreExposedForThePicker()
    {
        var row = NewRow();

        Assert.Same(Categories, row.RevitCategories);
        Assert.Empty(row.PartTypes);
    }
}
