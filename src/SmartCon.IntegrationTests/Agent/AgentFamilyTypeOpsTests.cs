using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
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
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// G.4 type-level family operations: duplicate/rename/replace family type,
/// load_family from a .rfa file and purge of unused families — the dry-run
/// plan, the confirmNames gate, read-back and undo_last round trips.
/// </summary>
public sealed class AgentFamilyTypeOpsTests : RevitApiTest
{
    private Document? _document;
    private FamilySeed? _seed;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _wallId;
    private long _wallTypeId;

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Before(Test)]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentTypeOps", out var reason);
        if (_seed is null)
            Skip.Test(reason ?? "Не удалось сидировать семейство");

        if (ModelSeed.HasWallType(Doc))
        {
            _transactions.RunInTransaction(Doc, "Seed system type wall", doc =>
            {
                var level = ModelSeed.CreateLevel(doc, 0.0);
                level.Name = "SC_AgentTypeOps_Level";
                _levelId = level.Id.GetValue();

                var wall = ModelSeed.TryCreateWall(doc, level.Id);
                if (wall is not null)
                {
                    _wallId = wall.Id.GetValue();
                    _wallTypeId = wall.GetTypeId().GetValue();
                }
            });
        }
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task Duplicate_DryRunThenApply_CreatesIndependentType()
    {
        var json = "{\"familyName\":\"" + Seed.FamilyName + "\",\"typeName\":\"" +
                   FamilySeed.Type1 + "\",\"newName\":\"Тип1_копия\"";

        var dry = Run(CreateDuplicateTool(), json + ",\"dryRun\":true}");
        await Assert.That(dry.Success).IsTrue();
        await Assert.That(Data(dry).GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Data(dry).TryGetProperty("changeSetId", out _)).IsFalse();
        await Assert.That(FindTypeOrNull("Тип1_копия")).IsNull();

        var apply = Run(CreateDuplicateTool(), json + ",\"dryRun\":false}");
        await Assert.That(apply.Success).IsTrue();
        var data = Data(apply);
        await Assert.That(data.GetProperty("typeId").GetInt64()).IsGreaterThan(0);
        await Assert.That(data.GetProperty("typeName").GetString()).IsEqualTo("Тип1_копия");

        var copy = FindTypeOrNull("Тип1_копия");
        await Assert.That(copy).IsNotNull();
        await Assert.That(copy!.Value.GetProperty("familyName").GetString())
            .IsEqualTo(Seed.FamilyName);

        var setOnCopy = Run(
            new SetFamilyParameterValuesTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"familyId\":" + Seed.FamilyId + ",\"items\":[{\"parameter\":\"" +
            FamilySeed.CodeParam + "\",\"typeName\":\"Тип1_копия\",\"value\":\"Z-9\"}]," +
            "\"dryRun\":false}");
        await Assert.That(setOnCopy.Success).IsTrue();

        await Assert.That(Seed.GetProjectParameter("Тип1_копия", FamilySeed.CodeParam)!.AsString())
            .IsEqualTo("Z-9");
        await Assert.That(Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.CodeParam)!.AsString())
            .IsEqualTo("A-1");
    }

    [Test]
    public async Task Duplicate_Guards_NameTakenAndForbiddenChars()
    {
        var taken = Run(
            CreateDuplicateTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"typeName\":\"" + FamilySeed.Type1 +
            "\",\"newName\":\"" + FamilySeed.Type2 + "\",\"dryRun\":false}");

        await Assert.That(taken.Success).IsFalse();
        await Assert.That(taken.ErrorCode).IsEqualTo("type_name_taken");

        var invalid = Run(
            CreateDuplicateTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"typeName\":\"" + FamilySeed.Type1 +
            "\",\"newName\":\"Тип{9}\",\"dryRun\":false}");

        await Assert.That(invalid.Success).IsFalse();
        await Assert.That(invalid.ErrorCode).IsEqualTo("type_name_invalid");
    }

    [Test]
    public async Task Rename_ApplyAndUndo_RestoresName()
    {
        var apply = Run(
            CreateRenameTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"typeName\":\"" + FamilySeed.Type1 +
            "\",\"newName\":\"Тип1_нов\",\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        await Assert.That(FindTypeOrNull("Тип1_нов")).IsNotNull();
        await Assert.That(FindTypeOrNull(FamilySeed.Type1)).IsNull();

        var undo = Run(
            new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(FindTypeOrNull(FamilySeed.Type1)).IsNotNull();
        await Assert.That(FindTypeOrNull("Тип1_нов")).IsNull();
    }

    [Test]
    public async Task Replace_BulkSwitchesInstancesAndUndoRestores()
    {
        var instanceIds = PlaceInstances(FamilySeed.Type1, 3);
        var type1Id = TypeId(FamilySeed.Type1);
        var type2Id = TypeId(FamilySeed.Type2);

        var apply = Run(
            CreateReplaceTool(),
            "{\"targetTypeId\":" + type2Id + ",\"fromTypeIds\":[" + type1Id + "],\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        await Assert.That(Data(apply).GetProperty("changed").GetArrayLength()).IsEqualTo(3);
        await Assert.That(CountElementsOnType(type2Id)).IsEqualTo(3);
        await Assert.That(CountElementsOnType(type1Id)).IsEqualTo(0);

        var undo = Run(
            new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(CountElementsOnType(type1Id)).IsEqualTo(3);
        await Assert.That(CountElementsOnType(type2Id)).IsEqualTo(0);
        foreach (var instanceId in instanceIds)
        {
            var instance = Doc.GetElement(ElementIdCompat.Create(instanceId));
            await Assert.That(instance).IsNotNull();
        }
    }

    [Test]
    public async Task Replace_InvalidElement_ReportedPerItem()
    {
        var instanceIds = PlaceInstances(FamilySeed.Type1, 1);
        var type1Id = TypeId(FamilySeed.Type1);
        var type2Id = TypeId(FamilySeed.Type2);

        var result = Run(
            CreateReplaceTool(),
            "{\"targetTypeId\":" + type2Id + ",\"elementIds\":[" + type1Id + "," +
            instanceIds[0] + "],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("failed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("failed")[0].GetProperty("errorCode").GetString())
            .IsEqualTo("type_not_valid_for_element");
        await Assert.That(CountElementsOnType(type2Id)).IsEqualTo(1);
    }

    [Test]
    public async Task LoadFamily_DuplicateRefused_NewLoadAndUndo()
    {
        var duplicate = Run(
            CreateLoadTool(),
            "{\"path\":\"" + Seed.RfaPath.Replace("\\", "\\\\") + "\",\"dryRun\":false}");

        await Assert.That(duplicate.Success).IsFalse();
        await Assert.That(duplicate.ErrorCode).IsEqualTo("family_already_loaded");
        await Assert.That(Data(duplicate).GetProperty("existingFamilyName").GetString())
            .IsEqualTo(Seed.FamilyName);

        var familyId = Seed.FamilyId;
        _transactions!.RunInTransaction(Doc, "Remove seeded family", doc =>
            doc.Delete(ElementIdCompat.Create(familyId)));
        await Assert.That(FindFamilyOrNull(Seed.FamilyName)).IsNull();

        var apply = Run(
            CreateLoadTool(),
            "{\"path\":\"" + Seed.RfaPath.Replace("\\", "\\\\") + "\",\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        var data = Data(apply);
        await Assert.That(data.GetProperty("symbolCount").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("familyName").GetString()).IsEqualTo(Seed.FamilyName);
        await Assert.That(FindTypeOrNull(FamilySeed.Type1)).IsNotNull();

        var undo = Run(
            new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(FindFamilyOrNull(Seed.FamilyName)).IsNull();
    }

    [Test]
    public async Task Purge_ConfirmGateAndUsedFamilyProtected()
    {
        var unusedSeed = FamilySeed.Create(Application, Doc, "SC_TypeOpsUnused", out var unusedReason);
        if (unusedSeed is null)
            Skip.Test(unusedReason ?? "Не удалось сидировать неиспользуемое семейство");

        try
        {
            PlaceInstances(FamilySeed.Type1, 1);

            var dry = Run(CreatePurgeTool(), "{\"dryRun\":true}");
            await Assert.That(dry.Success).IsTrue();
            var names = ReadNames(dry);
            await Assert.That(names).Contains("SC_TypeOpsUnused");
            await Assert.That(names.Contains(Seed.FamilyName)).IsFalse();

            var refused = Run(CreatePurgeTool(), "{\"dryRun\":false}");
            await Assert.That(refused.Success).IsFalse();
            await Assert.That(refused.ErrorCode).IsEqualTo("confirm_names_required");
            await Assert.That(FindFamilyOrNull("SC_TypeOpsUnused")).IsNotNull();

            var confirmJson = JsonSerializer.Serialize(names);
            var apply = Run(
                CreatePurgeTool(),
                "{\"confirmNames\":" + confirmJson + ",\"dryRun\":false}");

            await Assert.That(apply.Success).IsTrue();
            await Assert.That(Data(apply).GetProperty("undoSupported").GetBoolean()).IsFalse();
            await Assert.That(FindFamilyOrNull("SC_TypeOpsUnused")).IsNull();
            await Assert.That(FindFamilyOrNull(Seed.FamilyName)).IsNotNull();
        }
        finally
        {
            unusedSeed.Dispose();
        }
    }

    [Test]
    public async Task SystemTypes_ListWallTypes_WithCounts()
    {
        RequireSeededWall();

        var tool = new ListSystemTypesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"category\":\"OST_Walls\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var types = Data(result).GetProperty("types");
        await Assert.That(types.GetArrayLength()).IsGreaterThan(0);

        var found = false;
        foreach (var type in types.EnumerateArray())
        {
            if (type.GetProperty("typeId").GetInt64() != _wallTypeId)
                continue;

            found = true;
            await Assert.That(type.GetProperty("instanceCount").GetInt32()).IsEqualTo(1);
            await Assert.That(type.GetProperty("canBeDuplicated").GetBoolean()).IsTrue();
        }

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task System_DuplicateWallType_ReplaceAllWalls_UndoBothSteps()
    {
        RequireSeededWall();
        var wallTypeName = WallType().Name;
        var typeNameJson = JsonSerializer.Serialize(wallTypeName);

        var dry = Run(
            CreateDuplicateTool(),
            "{\"typeName\":" + typeNameJson + ",\"categoryName\":\"OST_Walls\"," +
            "\"newName\":\"SC_Стена_копия\",\"dryRun\":true}");
        await Assert.That(dry.Success).IsTrue();

        var duplicate = Run(
            CreateDuplicateTool(),
            "{\"typeName\":" + typeNameJson + ",\"categoryName\":\"OST_Walls\"," +
            "\"newName\":\"SC_Стена_копия\",\"dryRun\":false}");
        await Assert.That(duplicate.Success).IsTrue();
        var copyTypeId = Data(duplicate).GetProperty("typeId").GetInt64();
        await Assert.That(CountElementsOnType(copyTypeId)).IsEqualTo(0);

        var replace = Run(
            CreateReplaceTool(),
            "{\"targetTypeId\":" + copyTypeId + ",\"fromTypeIds\":[" + _wallTypeId +
            "],\"dryRun\":false}");
        await Assert.That(replace.Success).IsTrue();
        await Assert.That(Data(replace).GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(CountElementsOnType(copyTypeId)).IsEqualTo(1);
        await Assert.That(CountElementsOnType(_wallTypeId)).IsEqualTo(0);

        var undoReplace = RunUndo();
        await Assert.That(undoReplace.Success).IsTrue();
        await Assert.That(CountElementsOnType(_wallTypeId)).IsEqualTo(1);
        await Assert.That(CountElementsOnType(copyTypeId)).IsEqualTo(0);

        var undoDuplicate = RunUndo();
        await Assert.That(undoDuplicate.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(copyTypeId))).IsNull();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_wallId))).IsNotNull();
    }

    [Test]
    public async Task System_RenameWallType_ByCategoryName_AndUndo()
    {
        RequireSeededWall();
        var wallTypeName = WallType().Name;

        var rename = Run(
            CreateRenameTool(),
            "{\"typeName\":" + JsonSerializer.Serialize(wallTypeName) +
            ",\"categoryName\":\"OST_Walls\"," +
            "\"newName\":\"SC_Стена_нов\",\"dryRun\":false}");

        await Assert.That(rename.Success).IsTrue();
        await Assert.That(WallType().Name).IsEqualTo("SC_Стена_нов");
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_wallId))).IsNotNull();

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(WallType().Name).IsEqualTo(wallTypeName);
    }

    private void RequireSeededWall()
    {
        if (_wallId == 0)
            Skip.Test("В шаблоне нет типов стен или стену не удалось создать");
    }

    private WallType WallType() =>
        (WallType)Doc.GetElement(ElementIdCompat.Create(_wallTypeId));

    private ToolResult RunUndo() =>
        Run(
            new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"dryRun\":false}");

    private DuplicateFamilyTypeTool CreateDuplicateTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private RenameFamilyTypeTool CreateRenameTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private ReplaceFamilyTypeTool CreateReplaceTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private LoadFamilyTool CreateLoadTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private PurgeFamiliesTool CreatePurgeTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private static ToolResult Run(IAgentTool tool, string json) =>
        ((AgentWriteToolBase)tool).ExecuteForTests(
            Parse(json),
            approved: true,
            CancellationToken.None);

    private List<long> PlaceInstances(string typeName, int count)
    {
        var symbol = FindSymbol(typeName);
        if (symbol is null)
            Skip.Test("В семействе нет типа " + typeName);

        var ids = new List<long>();
        _transactions!.RunInTransaction(Doc, "Place instances", doc =>
        {
            if (!symbol.IsActive)
                symbol.Activate();

            for (var index = 0; index < count; index++)
            {
                var instance = doc.Create.NewFamilyInstance(
                    new XYZ(index * 3.0, 0, 0),
                    symbol,
                    StructuralType.NonStructural);
                ids.Add(instance.Id.GetValue());
            }
        });

        return ids;
    }

    private FamilySymbol? FindSymbol(string typeName)
    {
        var family = FindFamilyOrNull(Seed.FamilyName);
        if (family is null)
            return null;

        foreach (var symbolId in family.GetFamilySymbolIds())
        {
            if (Doc.GetElement(symbolId) is FamilySymbol symbol
                && string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase))
            {
                return symbol;
            }
        }

        return null;
    }

    private RevitFamily? FindFamilyOrNull(string familyName)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily family
                && string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return null;
    }

    private JsonElement? FindTypeOrNull(string typeName)
    {
        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyName\":\"" + Seed.FamilyName + "\"}"),
            CancellationToken.None);
        if (!result.Success || result.Data is null)
            return null;

        var types = Data(result).GetProperty("types");
        foreach (var type in types.EnumerateArray())
        {
            if (string.Equals(type.GetProperty("name").GetString(), typeName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return null;
    }

    private long TypeId(string typeName)
    {
        var symbol = FindSymbol(typeName);
        if (symbol is null)
            Skip.Test("В семействе нет типа " + typeName);

        return symbol.Id.GetValue();
    }

    private int CountElementsOnType(long typeId)
    {
        var count = 0;
        var target = ElementIdCompat.Create(typeId);
        foreach (var element in new FilteredElementCollector(Doc)
                     .WhereElementIsNotElementType())
        {
            if (element.GetTypeId() == target)
                count++;
        }

        return count;
    }

    private static List<string> ReadNames(ToolResult result)
    {
        var names = new List<string>();
        foreach (var name in Data(result).GetProperty("names").EnumerateArray())
            names.Add(name.GetString()!);
        return names;
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");
}
