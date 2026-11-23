using System.Text.Json;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Failure visibility of the album title block inheritance (review S1-R5):
/// a missing parameter and a rejected inherited value must add failed rows
/// and [Action] warnings instead of disappearing. White-box: drives the
/// internal seam of BuildSystemSheetsTool with a fabricated inheritance
/// dictionary on a real title block instance.
/// </summary>
public sealed partial class AgentScheduleHonestyTests
{
    [Test]
    public async Task ApplyInheritedTitleBlockValues_Failure_WarnsAndReportsFailedRows()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне нет титульного блока для листа");
        if (!TryAddTitleBlockInstanceParameter(MultiplicityParameter))
            Skip.Test("Не удалось добавить параметр в семейство титульного блока через EditFamily");

        var instance = FindFirstTitleBlockInstance();
        await Assert.That(instance).IsNotNull();

        // S1-R5: a missing parameter and a rejected value must not disappear —
        // each adds a failed row and a warning with an [Action].
        var warnings = new List<string>();
        var rows = BuildSystemSheetsTool.ApplyInheritedTitleBlockValues(
            instance!,
            new Dictionary<string, string>
            {
                [MultiplicityParameter] = "not-a-number",
                ["SC_Missing_Titleblock_Parameter"] = "1"
            },
            explicitParameters: null,
            sheetNumber: "SC-HN-99",
            warnings);

        var payload = AgentJson.Serialize(rows);
        var entries = payload.EnumerateArray().ToList();

        var rejected = entries.Single(row =>
            row.GetProperty("name").GetString() == MultiplicityParameter);
        await Assert.That(rejected.GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(rejected.GetProperty("errorCode").GetString()).IsEqualTo("invalid_value");

        var missing = entries.Single(row =>
            row.GetProperty("name").GetString() == "SC_Missing_Titleblock_Parameter");
        await Assert.That(missing.GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(missing.GetProperty("errorCode").GetString())
            .IsEqualTo("titleblock_parameter_not_found");

        await Assert.That(warnings.Count).IsEqualTo(2);
        foreach (var warning in warnings)
            await Assert.That(warning.Contains("[Action:", StringComparison.Ordinal)).IsTrue();
    }
}
