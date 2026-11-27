using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// The get_family_parameters batch must keep EVERY
/// requested family inside the context budget: a 20-family batch used to be
/// halved to 10 by the generic result trimmer, and the model re-read the same
/// ids (live case 2, rounds 4-7).
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    private const int BatchSeededFamilies = 20;
    private const int BatchSeededTypes = 90;

    [Test]
    public async Task GetFamilyParameters_Batch20Families_KeepsEveryFamilyWithinBudget()
    {
        var familyIds = SeedGenericFamilies(BatchSeededFamilies, BatchSeededTypes);
        var ids = string.Join(",", familyIds);
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        // The MUST shape of the brief: includeValues=false, every family back
        // inside the budget (compact rows or full payloads - both honest).
        var withoutValues = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyIds\":[" + ids + "],\"includeValues\":false,\"limit\":100}"),
            CancellationToken.None);
        await Assert.That(withoutValues.Success).IsTrue();
        await AssertEveryFamilyKept(withoutValues);
        await AssertSerializedWithinBudget(withoutValues);
        Probe("PROBE get_family_parameters batch (includeValues=false): full=" +
              ToolResultBudget.Serialize(withoutValues).Length);

        // The worst shape: values on 90 types per family - the full payload
        // cannot fit, so the tool answers with compact rows that STILL carry
        // all 20 families.
        var withValues = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyIds\":[" + ids + "],\"limit\":100}"),
            CancellationToken.None);
        await Assert.That(withValues.Success).IsTrue();
        await AssertEveryFamilyKept(withValues);
        await AssertSerializedWithinBudget(withValues);

        var data = Data(withValues);
        await Assert.That(data.GetProperty("compact").GetBoolean()).IsTrue();
        var compactRow = FirstOkFamily(data);
        await Assert.That(compactRow.GetProperty("typeCount").GetInt32())
            .IsEqualTo(BatchSeededTypes);
        var parameters = compactRow.GetProperty("parameters");
        await Assert.That(parameters.GetArrayLength()).IsGreaterThan(0);
        foreach (var parameter in parameters.EnumerateArray())
        {
            await Assert.That(string.IsNullOrEmpty(parameter.GetProperty("name").GetString()))
                .IsFalse();
            await Assert.That(parameter.TryGetProperty("valuesByType", out _)).IsFalse();
        }

        if (compactRow.TryGetProperty("parametersShown", out var shown))
            await Assert.That(shown.GetInt32()).IsGreaterThan(0);
        await Assert.That(withValues.Message).Contains("single-family call");
        await Assert.That(withValues.Message).Contains("familyId");
        // Nobody halved the families array behind the tool's back.
        await Assert.That(data.TryGetProperty("familiesReturned", out _)).IsFalse();
    }

    private static async Task AssertEveryFamilyKept(ToolResult result)
    {
        var families = Data(result).GetProperty("families");
        await Assert.That(families.GetArrayLength()).IsEqualTo(BatchSeededFamilies);
        foreach (var family in families.EnumerateArray())
        {
            await Assert.That(family.GetProperty("ok").GetBoolean()).IsTrue();
            await Assert.That(family.GetProperty("typeCount").GetInt32())
                .IsEqualTo(BatchSeededTypes);
        }
    }

    private static async Task AssertSerializedWithinBudget(ToolResult result) =>
        await Assert.That(ToolResultBudget.Serialize(result).Length)
            .IsLessThan(ToolResultBudget.DefaultMaxCharacters);

    private static JsonElement FirstOkFamily(JsonElement data)
    {
        foreach (var family in data.GetProperty("families").EnumerateArray())
        {
            if (family.GetProperty("ok").GetBoolean())
                return family;
        }

        throw new InvalidOperationException("В батче нет ни одного ok-семейства.");
    }
}
