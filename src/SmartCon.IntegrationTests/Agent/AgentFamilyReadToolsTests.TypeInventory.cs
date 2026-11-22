using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Inventory reads of list_family_types: the compact record, direct type selection
/// and the per-type parameter values.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    private const string CompactTypesHint =
        "Compact records show typeId, name, familyId, familyName; pass fields=[...] for other fields " +
        "(availableFields).";

    private static readonly string[] SevenTypeFields =
    {
        "typeId", "name", "familyId", "familyName", "isPlaced", "parameters", "parameterCount"
    };

    private static readonly string[] CompactTypeFields = { "typeId", "name", "familyId", "familyName" };

    private static readonly string[] FullTypeFields =
    {
        "typeId", "name", "familyId", "familyName", "isPlaced", "parameters", "parameterCount",
        "parametersTruncated"
    };

    [Test]
    public async Task ListFamilyTypes_DefaultRecord_IsCompactAndOldSwitchesKeepTheirFields()
    {
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));

        var defaults = tool.ExecuteForTests(Doc, FamilyArgs(), CancellationToken.None);
        await Assert.That(defaults.Success).IsTrue();
        var defaultData = Data(defaults);
        await Assert.That(Names(defaultData.GetProperty("availableFields"))).IsEqualTo(Names(SevenTypeFields));
        await Assert.That(Names(defaultData.GetProperty("fields"))).IsEqualTo(Names(CompactTypeFields));
        await Assert.That(defaultData.GetProperty("types").GetArrayLength()).IsEqualTo(2);
        foreach (var record in defaultData.GetProperty("types").EnumerateArray())
            await Assert.That(Names(record)).IsEqualTo(Names(CompactTypeFields));

        var full = tool.ExecuteForTests(Doc, FamilyArgs(",\"compact\":false"), CancellationToken.None);
        await Assert.That(full.Success).IsTrue();
        await Assert.That(Names(Data(full).GetProperty("fields"))).IsEqualTo(Names(FullTypeFields));
        foreach (var record in Data(full).GetProperty("types").EnumerateArray())
            await Assert.That(Names(record)).IsEqualTo(Names(FullTypeFields));

        var placed = tool.ExecuteForTests(Doc, FamilyArgs(",\"includePlaced\":true"), CancellationToken.None);
        foreach (var record in Data(placed).GetProperty("types").EnumerateArray())
            await Assert.That(Names(record)).IsEqualTo("typeId,name,familyId,familyName,isPlaced");

        var parameters = tool.ExecuteForTests(
            Doc, FamilyArgs(",\"includeParameters\":true,\"parameterLimit\":3"), CancellationToken.None);
        var parameterData = Data(parameters);
        await Assert.That(parameterData.GetProperty("parametersTruncated").GetBoolean()).IsTrue();
        foreach (var record in parameterData.GetProperty("types").EnumerateArray())
        {
            await Assert.That(Names(record)).IsEqualTo("typeId,name,familyId,familyName,parameters,parameterCount");
            await Assert.That(record.GetProperty("parameters").GetArrayLength()).IsEqualTo(3);
        }

        var explicitFields = tool.ExecuteForTests(
            Doc, FamilyArgs(",\"fields\":[\"name\",\"parameters\"],\"parameterLimit\":3"), CancellationToken.None);
        foreach (var record in Data(explicitFields).GetProperty("types").EnumerateArray())
        {
            await Assert.That(Names(record)).IsEqualTo("name,parameters");
            await Assert.That(record.GetProperty("parameters").GetArrayLength()).IsEqualTo(3);
        }

        var withoutParameters = tool.ExecuteForTests(
            Doc, FamilyArgs(",\"fields\":[\"name\",\"parameters\"],\"includeParameters\":false"), CancellationToken.None);
        await Assert.That(Names(Data(withoutParameters).GetProperty("fields"))).IsEqualTo("name");
        foreach (var record in Data(withoutParameters).GetProperty("types").EnumerateArray())
            await Assert.That(Names(record)).IsEqualTo("name");

        var unknown = tool.ExecuteForTests(Doc, FamilyArgs(",\"fields\":[\"name\",\"width\"]"), CancellationToken.None);
        await Assert.That(unknown.Success).IsFalse();
        await Assert.That(unknown.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(unknown.Message!.StartsWith("Unknown field(s): width.", StringComparison.Ordinal)).IsTrue();
        foreach (var field in SevenTypeFields)
            await Assert.That(unknown.Message.Contains(field, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ListFamilyTypes_ParameterNames_ReturnsTypeValuesAndReportsMissingParameters()
    {
        var genericFamilyId = SeedGenericFamilies(1, 2)[0];
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyIds\":[" + Seed.FamilyId + "," + genericFamilyId +
                  "],\"parameterNames\":[\"Ширина\",\"марка\",\"НетТакогоПараметра\"],\"fields\":[\"name\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(4);
        await Assert.That(data.GetProperty("valueScope").GetString()).IsEqualTo("type");
        await Assert.That(Names(data.GetProperty("missingParameters"))).IsEqualTo("НетТакогоПараметра");
        await Assert.That(Names(data.GetProperty("fields"))).IsEqualTo("name");

        var widths = new List<string>();
        var marks = new List<string>();
        var withoutValues = 0;
        foreach (var record in data.GetProperty("types").EnumerateArray())
        {
            if (!record.TryGetProperty("values", out var values))
            {
                withoutValues++;
                await Assert.That(Names(record)).IsEqualTo("name");
                continue;
            }

            await Assert.That(Names(record)).IsEqualTo("name,values");
            await Assert.That(Names(values)).IsEqualTo("Ширина,Марка");
            widths.Add(values.GetProperty("Ширина").GetString()!);
            marks.Add(values.GetProperty("Марка").GetString()!);
        }

        await Assert.That(widths.Count).IsEqualTo(2);
        await Assert.That(withoutValues).IsEqualTo(2);
        await Assert.That(string.Join("|", marks)).IsEqualTo("A-1|B-2");
        await Assert.That(widths[0].Contains("1000", StringComparison.Ordinal)).IsTrue();
        await Assert.That(widths[1].Contains("1500", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Message).Contains("No returned type has parameter(s): НетТакогоПараметра.");
    }

    [Test]
    public async Task ListFamilyTypes_InstanceScope_ReadsValuesFromPlacedInstances()
    {
        var symbols = SeedSymbols();
        using (var transaction = new Transaction(Doc, "Place inventory instances"))
        {
            transaction.Start();
            PlaceWithNote(symbols[FamilySeed.Type1], new XYZ(0, 0, 0), "instance-a");
            PlaceWithNote(symbols[FamilySeed.Type1], new XYZ(5, 0, 0), "instance-b");
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Транзакция размещения экземпляров не завершилась");
        }

        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));
        var request = ",\"parameterNames\":[\"Примечание_экз\",\"Ширина\"],\"valueScope\":\"instance\"," +
                      "\"fields\":[\"name\",\"isPlaced\"]";
        var result = tool.ExecuteForTests(Doc, FamilyArgs(request), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("valueScope").GetString()).IsEqualTo("instance");
        await Assert.That(Names(data.GetProperty("missingParameters"))).IsEqualTo("Ширина");
        var records = data.GetProperty("types").EnumerateArray().ToList();
        await Assert.That(records.Count).IsEqualTo(2);

        // Тип1: two placed instances disagree → value of the first one + valuesVary.
        await Assert.That(records[0].GetProperty("name").GetString()).IsEqualTo(FamilySeed.Type1);
        await Assert.That(Names(records[0])).IsEqualTo("name,isPlaced,values,valuesVary");
        await Assert.That(records[0].GetProperty("isPlaced").GetBoolean()).IsTrue();
        await Assert.That(records[0].GetProperty("values").GetProperty("Примечание_экз").GetString())
            .IsEqualTo("instance-a");

        // Тип2: no placed instance → no values at all.
        await Assert.That(Names(records[1])).IsEqualTo("name,isPlaced");
        await Assert.That(records[1].GetProperty("isPlaced").GetBoolean()).IsFalse();

        // The same names read in type scope: the instance parameter is missing there.
        var typeScope = tool.ExecuteForTests(Doc, FamilyArgs(request.Replace("\"instance\"", "\"type\"")), CancellationToken.None);
        var typeData = Data(typeScope);
        await Assert.That(Names(typeData.GetProperty("missingParameters"))).IsEqualTo("Примечание_экз");
        var widths = new List<string>();
        foreach (var record in typeData.GetProperty("types").EnumerateArray())
        {
            await Assert.That(Names(record)).IsEqualTo("name,isPlaced,values");
            await Assert.That(Names(record.GetProperty("values"))).IsEqualTo("Ширина");
            widths.Add(record.GetProperty("values").GetProperty("Ширина").GetString()!);
        }

        await Assert.That(widths.Count).IsEqualTo(2);
        await Assert.That(widths[0].Contains("1000", StringComparison.Ordinal)).IsTrue();
        await Assert.That(widths[1].Contains("1500", StringComparison.Ordinal)).IsTrue();

        using (var transaction = new Transaction(Doc, "Place inventory instance"))
        {
            transaction.Start();
            PlaceWithNote(symbols[FamilySeed.Type2], new XYZ(10, 0, 0), "instance-c");
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Транзакция размещения экземпляра не завершилась");
        }

        var placed = tool.ExecuteForTests(Doc, FamilyArgs(request), CancellationToken.None);
        records = Data(placed).GetProperty("types").EnumerateArray().ToList();
        await Assert.That(Names(records[1])).IsEqualTo("name,isPlaced,values");
        await Assert.That(records[1].GetProperty("values").GetProperty("Примечание_экз").GetString())
            .IsEqualTo("instance-c");
        await Assert.That(Names(records[0])).IsEqualTo("name,isPlaced,values,valuesVary");
    }

    [Test]
    public async Task ListFamilyTypes_TypeIds_SelectTypesDirectlyAndReportUnknownIds()
    {
        var symbols = SeedSymbols();
        var type1 = symbols[FamilySeed.Type1].Id.GetValue();
        var type2 = symbols[FamilySeed.Type2].Id.GetValue();
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));

        var direct = tool.ExecuteForTests(
            Doc, Parse("{\"typeIds\":[" + type2 + "," + type1 + "," + type1 + "]}"), CancellationToken.None);
        await Assert.That(direct.Success).IsTrue();
        var directData = Data(direct);
        await Assert.That(directData.GetProperty("total").GetInt32()).IsEqualTo(2);
        await Assert.That(directData.GetProperty("familyCount").GetInt32()).IsEqualTo(1);
        await Assert.That(directData.GetProperty("familyId").GetInt64()).IsEqualTo(Seed.FamilyId);
        var names = new List<string>();
        foreach (var record in directData.GetProperty("types").EnumerateArray())
            names.Add(record.GetProperty("name").GetString()!);
        await Assert.That(string.Join("|", names)).IsEqualTo(FamilySeed.Type1 + "|" + FamilySeed.Type2);

        var unknown = tool.ExecuteForTests(
            Doc, Parse("{\"typeIds\":[" + type1 + ",999999999]}"), CancellationToken.None);
        await Assert.That(unknown.Success).IsFalse();
        await Assert.That(unknown.ErrorCode).IsEqualTo("type_not_found");
        await Assert.That(Names(Data(unknown).GetProperty("missing"))).IsEqualTo("999999999");
        await Assert.That(unknown.Message).Contains("999999999");

        var none = tool.ExecuteForTests(Doc, Parse("{\"typeIds\":[999999999]}"), CancellationToken.None);
        await Assert.That(none.Success).IsFalse();
        await Assert.That(none.ErrorCode).IsEqualTo("type_not_found");
        await Assert.That(none.Message!.StartsWith("None of the typeIds resolved", StringComparison.Ordinal)).IsTrue();

        var empty = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);
        await Assert.That(empty.Success).IsFalse();
        await Assert.That(empty.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(empty.Message).Contains("typeIds");
    }

    [Test]
    public async Task ListFamilyTypes_LargeResult_HintsFieldsAndNarrowing()
    {
        var familyId = SeedGenericFamilies(1, 60)[0];
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));

        var plain = tool.ExecuteForTests(Doc, FamilyArgsOf(familyId, ""), CancellationToken.None);
        await Assert.That(plain.Success).IsTrue();
        await Assert.That(Data(plain).GetProperty("returned").GetInt32()).IsEqualTo(60);
        await Assert.That(plain.Message).Contains(CompactTypesHint);
        await Assert.That(plain.Message).Contains("Pass parameterNames=[...] to add parameter values.");

        var withNames = tool.ExecuteForTests(
            Doc, FamilyArgsOf(familyId, ",\"parameterNames\":[\"НетТакогоПараметра\"]"), CancellationToken.None);
        await Assert.That(withNames.Message).Contains(CompactTypesHint);
        await Assert.That(withNames.Message).DoesNotContain("Pass parameterNames");

        var small = tool.ExecuteForTests(Doc, FamilyArgs(), CancellationToken.None);
        await Assert.That(small.Message).DoesNotContain("Compact records show");

        var paged = tool.ExecuteForTests(Doc, FamilyArgsOf(familyId, ",\"limit\":10"), CancellationToken.None);
        var pagedData = Data(paged);
        await Assert.That(pagedData.GetProperty("returned").GetInt32()).IsEqualTo(10);
        await Assert.That(pagedData.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(pagedData.GetProperty("nextOffset").GetInt32()).IsEqualTo(10);
        await Assert.That(paged.Message).Contains(", next offset 10.");
        await Assert.That(paged.Message).Contains("Narrow with typeIds or fewer fields.");
    }

    private JsonElement FamilyArgs() => FamilyArgs("");

    /// <summary>Arguments for the seeded family: <paramref name="properties"/> is empty or starts with a comma.</summary>
    private JsonElement FamilyArgs(string properties) => FamilyArgsOf(Seed.FamilyId, properties);

    private static JsonElement FamilyArgsOf(long familyId, string properties) =>
        Parse("{\"familyId\":" + familyId.ToString(System.Globalization.CultureInfo.InvariantCulture) + properties + "}");

    private Dictionary<string, FamilySymbol> SeedSymbols()
    {
        RevitFamily? family = null;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily candidate
                && string.Equals(candidate.Name, Seed.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                family = candidate;
            }
        }

        var symbols = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
        if (family is null)
            return symbols;

        foreach (var id in family.GetFamilySymbolIds())
        {
            if (Doc.GetElement(id) is FamilySymbol symbol)
                symbols.Add(symbol.Name, symbol);
        }

        return symbols;
    }

    private void PlaceWithNote(FamilySymbol symbol, XYZ location, string note)
    {
        if (!symbol.IsActive)
            symbol.Activate();

        var instance = Doc.Create.NewFamilyInstance(location, symbol, StructuralType.NonStructural);
        var parameter = instance.LookupParameter(FamilySeed.InstanceParam);
        if (parameter is null || !parameter.Set(note))
            throw new InvalidOperationException("Примечание_экз не записано в размещённый экземпляр");
    }
}
