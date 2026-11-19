using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Read side of the family editing pack (H1d): list_families, list_family_types,
/// get_family_parameters, find_families_with_parameter.
/// </summary>
public sealed class AgentFamilyReadToolsTests : RevitApiTest
{
    private Document? _document;
    private FamilySeed? _seed;

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Before(Test)]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentFamilyRead", out var reason);
        if (_seed is null)
            Skip.Test(reason ?? "Не удалось сидировать семейство");
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task ListFamilies_ReturnsSeededFamilyWithTypeCount()
    {
        var tool = new ListFamiliesTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var family = FindFamily(Data(result), Seed.FamilyId);
        await Assert.That(family.GetProperty("name").GetString()).IsEqualTo(Seed.FamilyName);
        await Assert.That(family.GetProperty("typeCount").GetInt32()).IsEqualTo(2);
        await Assert.That(family.GetProperty("isEditable").GetBoolean()).IsTrue();
        await Assert.That(family.GetProperty("isInPlace").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task ListFamilies_NameFilterMatchesLocalizedText()
    {
        var tool = new ListFamiliesTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"nameContains\":\"sc_agentfamily\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var families = Data(result).GetProperty("families");
        await Assert.That(families.GetArrayLength()).IsEqualTo(1);
        await Assert.That(
            families[0].GetProperty("familyId").GetInt64()).IsEqualTo(Seed.FamilyId);
    }

    [Test]
    public async Task ListFamilyTypes_ReturnsBothTypesIncludingUnplaced()
    {
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + ",\"parameterLimit\":100}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(2);

        var types = data.GetProperty("types");
        var names = new List<string>();
        var allUnplaced = true;
        foreach (var type in types.EnumerateArray())
        {
            names.Add(type.GetProperty("name").GetString()!);
            allUnplaced &= !type.GetProperty("isPlaced").GetBoolean();
            var parameters = type.GetProperty("parameters");
            var hasWidth = false;
            foreach (var parameter in parameters.EnumerateArray())
            {
                if (parameter.GetString() == FamilySeed.WidthParam)
                    hasWidth = true;
            }

            await Assert.That(hasWidth).IsTrue();
        }

        await Assert.That(names.Contains(FamilySeed.Type1)).IsTrue();
        await Assert.That(names.Contains(FamilySeed.Type2)).IsTrue();
        await Assert.That(allUnplaced).IsTrue();
    }

    [Test]
    public async Task ListFamilyTypes_UnknownFamily_ReturnsFamilyNotFound()
    {
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":987654321}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("family_not_found");
    }

    [Test]
    public async Task GetFamilyParameters_ReportsFormulaSharedFlagsAndValues()
    {
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + "}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("typeCount").GetInt32()).IsEqualTo(2);

        var area = FindParameter(data, FamilySeed.AreaParam);
        await Assert.That(area.GetProperty("isDeterminedByFormula").GetBoolean()).IsTrue();
        await Assert.That(area.GetProperty("formula").GetString()).Contains("*");
        await Assert.That(area.GetProperty("canSetValue").GetBoolean()).IsFalse();
        await Assert.That(area.GetProperty("valuesByType").GetArrayLength()).IsEqualTo(2);

        var code = FindParameter(data, FamilySeed.CodeParam);
        await Assert.That(code.GetProperty("scope").GetString()).IsEqualTo("type");
        var codes = new List<string>();
        foreach (var value in code.GetProperty("valuesByType").EnumerateArray())
            codes.Add(value.GetProperty("value").GetString() ?? string.Empty);
        await Assert.That(codes.Contains("A-1")).IsTrue();
        await Assert.That(codes.Contains("B-2")).IsTrue();

        var instance = FindParameter(data, FamilySeed.InstanceParam);
        await Assert.That(instance.GetProperty("scope").GetString()).IsEqualTo("instance");
        await Assert.That(instance.GetProperty("isInstance").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task GetFamilyParameters_PaginationIsHonest()
    {
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + ",\"limit\":2,\"offset\":0}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("returned").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("nextOffset").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task GetFamilyParameters_InvalidScope_ReturnsInvalidArguments()
    {
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + ",\"scope\":\"bogus\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task GetFamilyParameters_ClosesFamilyDocument()
    {
        var before = Application.Documents.Size;
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        var first = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + "}"),
            CancellationToken.None);
        var second = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + "}"),
            CancellationToken.None);

        await Assert.That(first.Success).IsTrue();
        await Assert.That(second.Success).IsTrue();
        await Assert.That(Application.Documents.Size).IsEqualTo(before);
    }

    [Test]
    public async Task FindFamiliesWithParameter_FindsByParameterName()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"Ширина\",\"match\":\"exact\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(ContainsFamily(data, Seed.FamilyId)).IsTrue();
    }

    [Test]
    public async Task FindFamiliesWithParameter_FindsByValueContains()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"Марка\",\"valueContains\":\"A-1\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        var family = FindFamily(data, Seed.FamilyId);
        var matched = family.GetProperty("matchedTypes");
        await Assert.That(matched.GetArrayLength()).IsEqualTo(1);
        await Assert.That(
            matched[0].GetProperty("value").GetString()!).Contains("A-1");
    }

    [Test]
    public async Task FindFamiliesWithParameter_UnknownParameter_ReturnsEmptyResult()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"НетТакогоПараметра\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("typesScanned").GetInt32()).IsGreaterThan(0);
    }

    [Test]
    public async Task FindFamiliesWithParameter_MissingArgument_ReturnsInvalidArguments()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");

    private static JsonElement FindFamily(JsonElement data, long familyId)
    {
        foreach (var family in data.GetProperty("families").EnumerateArray())
        {
            if (family.GetProperty("familyId").GetInt64() == familyId)
                return family;
        }

        throw new InvalidOperationException("Family " + familyId + " is missing in the result.");
    }

    private static bool ContainsFamily(JsonElement data, long familyId)
    {
        foreach (var family in data.GetProperty("families").EnumerateArray())
        {
            if (family.GetProperty("familyId").GetInt64() == familyId)
                return true;
        }

        return false;
    }

    private static JsonElement FindParameter(JsonElement data, string name)
    {
        foreach (var parameter in data.GetProperty("parameters").EnumerateArray())
        {
            if (string.Equals(parameter.GetProperty("name").GetString(), name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        throw new InvalidOperationException("Parameter '" + name + "' is missing in the result.");
    }
}