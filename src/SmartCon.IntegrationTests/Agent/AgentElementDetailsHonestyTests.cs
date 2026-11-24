using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Honesty of get_element_details on batches: full type dumps must not evict
/// the requested elements from the context budget (live case 4, round 3), and
/// an empty parameter must carry an explicit value marker instead of silently
/// losing its value key (live case 7, round 8).
/// </summary>
public sealed class AgentElementDetailsHonestyTests : RevitApiTest
{
    private Document? _document;
    private List<long> _wallIds = new();
    private string _markName = "Марка";

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        var wallTypes = new FilteredElementCollector(Doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .ToList();
        if (wallTypes.Count == 0)
            Skip.Test("В шаблоне проекта нет типов стен");

        // One wall per available type: every element brings its own full type
        // dump when includeTypeParameters=true - the shape that overflowed the
        // budget in the live run. Some template types reject Wall.Create with a
        // silent roll-back (#104): each wall gets its own transaction and a
        // rejected type is simply skipped.
        ElementId levelId;
        using (var transaction = new Transaction(Doc, "Seed level"))
        {
            transaction.Start();
            levelId = Level.Create(Doc, 3.0).Id;
            if (transaction.Commit() != TransactionStatus.Committed)
                Skip.Test("Не удалось создать уровень для сида стен");
        }

        var offset = 0;
        foreach (var wallType in wallTypes)
        {
            if (_wallIds.Count >= 10)
                break;

            long wallId;
            string markName = _markName;
            using (var transaction = new Transaction(Doc, "Seed wall " + _wallIds.Count))
            {
                transaction.Start();
                var line = Line.CreateBound(
                    new XYZ(offset, 0, 0), new XYZ(offset + 10, 0, 0));
                var wall = Wall.Create(
                    Doc, line, wallType.Id, levelId, 3.0, 0.0, false, false);
                if (transaction.Commit() != TransactionStatus.Committed)
                    continue;

                wallId = wall.Id.GetValue();
                markName = wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                    ?.Definition?.Name ?? markName;
            }

            if (_wallIds.Count == 0)
                _markName = markName;
            _wallIds.Add(wallId);
            offset += 20;
        }

        if (_wallIds.Count == 0)
            Skip.Test("Ни один тип стены из шаблона не создался для сида");
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task GetElementDetails_TypeDumpsDoNotEvictRequestedElements()
    {
        var ids = string.Join(",", _wallIds);
        var tool = new GetElementDetailsTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"elementIds\":[" + ids +
                  "],\"includeTypeParameters\":true,\"maxParametersPerElement\":200}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        var returned = new List<long>();
        foreach (var element in data.GetProperty("elements").EnumerateArray())
            returned.Add(element.GetProperty("elementId").GetInt64());
        await Assert.That(returned.Count).IsEqualTo(_wallIds.Count);
        foreach (var requested in _wallIds)
            await Assert.That(returned).Contains(requested);

        // The whole serialized result stays within the context budget, so the
        // generic trimmer never gets a chance to halve the elements array.
        var json = ToolResultBudget.Serialize(result);
        await Assert.That(json.Length).IsLessThan(ToolResultBudget.DefaultMaxCharacters);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(_wallIds.Count);
        await Assert.That(data.TryGetProperty("elementsReturned", out _)).IsFalse();
        // The compaction, when it ran, is reported honestly next to the data.
        if (data.TryGetProperty("parametersCompacted", out var compacted))
        {
            await Assert.That(compacted.GetProperty("compactedTo").GetInt32())
                .IsLessThan(200);
            await Assert.That(result.Message).Contains("pass parameterNames");
        }
    }

    [Test]
    public async Task GetElementDetails_EmptyParameter_CarriesExplicitValue()
    {
        var tool = new GetElementDetailsTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"elementIds\":[" + _wallIds[0] +
                  "],\"parameterNames\":[\"ALL_MODEL_MARK\",\"ALL_MODEL_TYPE_MARK\"]," +
                  "\"onlyNonEmpty\":false}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var element = Data(result).GetProperty("elements")[0];
        var emptySeen = 0;
        foreach (var parameter in element.GetProperty("instanceParameters").EnumerateArray())
        {
            if (parameter.GetProperty("name").GetString() != _markName)
                continue;

            emptySeen++;
            // "Empty" must be explicit: value:"" plus hasValue:false, distinct
            // from a value that failed to read (both keys omitted).
            await Assert.That(parameter.GetProperty("value").GetString()).IsEqualTo(string.Empty);
            await Assert.That(parameter.GetProperty("hasValue").GetBoolean()).IsFalse();
        }

        // The seeded walls carry no mark: the empty row must be present, not
        // silently dropped between the two parameterNames lookups. Both names
        // resolve (mark on the element, type mark on its type), so the
        // not-found list must be absent.
        await Assert.That(emptySeen).IsEqualTo(1);
        var hasNotFound = Data(result).TryGetProperty("parameterNamesNotFound", out var notFound)
                          && notFound.ValueKind != JsonValueKind.Null;
        await Assert.That(hasNotFound).IsFalse();
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");
}
