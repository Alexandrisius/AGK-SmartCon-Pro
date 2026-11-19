using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Partial-failure, all-failed and shared-definition edge cases of the family
/// write tools (split from the main test class to keep files under 600 lines).
/// </summary>
public sealed partial class AgentFamilyWriteToolsTests
{
    [Test]
    public async Task SetValues_PartialFailure_KeepsChangedAndChangeSet()
    {
        var result = Run(
            CreateSetValuesTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"items\":[" +
            "{\"parameter\":\"Ширина\",\"typeName\":\"Тип1\",\"value\":\"1100\"," +
            "\"unit\":\"mm\"}," +
            "{\"parameter\":\"Площадь\",\"typeName\":\"Тип1\",\"value\":\"10\"," +
            "\"unit\":\"m2\"}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("partial_failure");
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("failed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsTrue();

        var width = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        await Assert.That(System.Math.Abs(width!.AsDouble() - 1100.0 / 304.8) < 1e-9).IsTrue();
    }

    [Test]
    public async Task SetValues_AllFailed_RollsBackAndWritesNoChangeSet()
    {
        var result = Run(
            CreateSetValuesTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"items\":[" +
            "{\"parameter\":\"Площадь\",\"typeName\":\"Тип1\",\"value\":\"10\"," +
            "\"unit\":\"m2\"}," +
            "{\"parameter\":\"Давление\",\"typeName\":\"Тип1\",\"value\":\"1\"," +
            "\"unit\":\"bar\"}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("failed").GetArrayLength()).IsEqualTo(2);
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsFalse();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);

        var width = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        await Assert.That(System.Math.Abs(width!.AsDouble() - 1000.0 / 304.8) < 1e-9).IsTrue();
    }

    [Test]
    public async Task SetValues_InstanceParameter_ReportsInstanceSkipped()
    {
        var result = Run(
            CreateSetValuesTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"items\":[" +
            "{\"parameter\":\"Примечание_экз\",\"scope\":\"instance\"," +
            "\"typeName\":\"Тип1\",\"value\":\"примечание\"}],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var readBack = Data(result).GetProperty("readBack");
        await Assert.That(readBack.GetProperty("instanceSkipped").GetInt32())
            .IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task SetValues_ForeignTypeId_ReturnsInvalidValue()
    {
        using var other = FamilySeed.Create(
            Application,
            Doc,
            "SC_AgentFamilyOther",
            out var reason);
        if (other is null)
            Skip.Test(reason ?? "Не удалось сидировать второе семейство");

        var foreignTypeId = FindFirstTypeId(other.FamilyName);
        var result = Run(
            CreateSetValuesTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"items\":[" +
            "{\"parameter\":\"Ширина\",\"typeId\":" + foreignTypeId + "," +
            "\"value\":\"1000\",\"unit\":\"mm\"}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("invalid_value");
    }

    [Test]
    public async Task AddParameter_FamilyIdAndNameMismatch_ReturnsInvalidValue()
    {
        var result = Run(
            CreateAddTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"familyName\":\"SC_AgentFamilyOther\"," +
            "\"name\":\"Заказчик\",\"storageType\":\"text\",\"dryRun\":true}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_value");
    }

    [Test]
    public async Task AddParameter_Shared_FindsDefinitionInAnotherGroup()
    {
        using var scope = CreateSharedFileScope();
        var file = Application.OpenSharedParameterFile();
        await Assert.That(file).IsNotNull();
        var group = file!.Groups.Create("OtherGroup");
        var definition = group.Definitions.Create(
            new ExternalDefinitionCreationOptions("ОбщийКод", SpecTypeId.String.Text));
        var guid = ((ExternalDefinition)definition).GUID;

        var result = Run(
            CreateAddTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"name\":\"ОбщийКод\"," +
            "\"storageType\":\"text\",\"isShared\":true,\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        var parameter = Data(result).GetProperty("parameter");
        await Assert.That(
            parameter.TryGetProperty("wouldCreateDefinition", out _)).IsFalse();
        await Assert.That(parameter.GetProperty("guid").GetString())
            .IsEqualTo(guid.ToString("D"));
    }

    private long FindFirstTypeId(string familyName)
    {
        var symbols = new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol));
        foreach (var element in symbols)
        {
            if (element is FamilySymbol symbol
                && string.Equals(symbol.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return symbol.Id.GetValue();
            }
        }

        return -1;
    }
}
