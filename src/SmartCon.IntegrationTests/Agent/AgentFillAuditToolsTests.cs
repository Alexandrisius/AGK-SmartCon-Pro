using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Fill-rate audit tools of benchmark v2 (cases 2/7/8): audit_fill_rate
/// (on-sheet schedule as the mandatory parameter source, off-sheet bait,
/// three empty buckets), group_families_by_shared_parameters (two ФОП
/// groups + unplaced list), list_type_parameter_candidates (empty identity
/// data strings) and the guid/parameterName/placedOnSheet extension of
/// get_schedule_definition / list_schedules.
/// </summary>
public sealed partial class AgentFillAuditToolsTests : RevitApiTest
{
    private const string LevelName = "SC_FillAuditLevel";
    private const string SheetScheduleName = "СК_Ведомость_листовая";
    private const string BaitScheduleName = "СК_Черновая";
    private const string SheetNumber = "SC-FA-01";
    private const string NameParameter = "СК_Наименование";
    private const string UnitParameter = "СК_Единица";
    private const string BaitParameter = "СК_Комментарий_приманки";
    private const string FamilyA = "СК_ФОП_А";
    private const string FamilyB = "СК_ФОП_Б";
    private const string SharedGroupName = "СК_Аудит";

    private static readonly Guid NameGuid = new("5d1e2f3a-1111-4a2b-9c3d-0a1b2c3d4e5f");
    private static readonly Guid UnitGuid = new("5d1e2f3a-2222-4a2b-9c3d-0a1b2c3d4e5f");
    private static readonly Guid BaitGuid = new("5d1e2f3a-3333-4a2b-9c3d-0a1b2c3d4e5f");
    private static readonly Guid GroupAGuid = new("5d1e2f3a-4444-4a2b-9c3d-0a1b2c3d4e5f");
    private static readonly Guid GroupBGuid = new("5d1e2f3a-5555-4a2b-9c3d-0a1b2c3d4e5f");

    private Document? _document;
    private RevitTransactionService? _transactions;
    private SharedFileScope? _sharedFile;
    private readonly List<long> _wallIds = new();
    private long _sheetScheduleId;
    private long _baitScheduleId;
    private bool _seeded;
    private bool _sheetCreated;
    private bool _familiesSeeded;
    private readonly List<FamilySeed> _seeds = new();

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasWallType(Doc))
            Skip.Test("В шаблоне проекта нет типа стены");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _sharedFile = SharedFileScope.Create(Application);
        var definitions = SeedSharedDefinitions();

        _transactions.RunInTransaction(Doc, "Seed walls, bindings, schedules", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;

            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, index * 4.0);
                if (wall is null)
                    continue;

                _wallIds.Add(wall.Id.GetValue());
            }

            if (_wallIds.Count < 3)
                return;

            BindSharedParameter(doc, definitions.Name);
            BindSharedParameter(doc, definitions.Unit);
            BindSharedParameter(doc, definitions.Bait);

            // Fill pattern: wall0 full, wall1 empty string + no value, wall2
            // filled name + empty string unit. Mark only on wall0 and wall2.
            SetWallValue(0, definitions.Name, "Насос");
            SetWallValue(0, definitions.Unit, "шт");
            SetWallValue(1, definitions.Name, string.Empty);
            SetWallValue(2, definitions.Name, "Задвижка");
            SetWallValue(2, definitions.Unit, string.Empty);
            Wall(0).get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("M-1");
            Wall(2).get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("M-3");

            var sheetSchedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_Walls));
            sheetSchedule.Name = SheetScheduleName;
            sheetSchedule.Definition.AddField(
                ScheduleFieldType.Instance, SharedId(doc, definitions.Name));
            sheetSchedule.Definition.AddField(
                ScheduleFieldType.Instance, SharedId(doc, definitions.Unit));
            sheetSchedule.Definition.AddField(
                ScheduleFieldType.Instance,
                new ElementId(BuiltInParameter.ALL_MODEL_MARK));
            _sheetScheduleId = sheetSchedule.Id.GetValue();

            var baitSchedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_Walls));
            baitSchedule.Name = BaitScheduleName;
            baitSchedule.Definition.AddField(
                ScheduleFieldType.Instance, SharedId(doc, definitions.Bait));
            _baitScheduleId = baitSchedule.Id.GetValue();

            var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
            if (titleBlockId is null)
            {
                var titleBlockPath = ModelSeed.FindTitleBlockFamilyFile(
                    doc.Application.VersionNumber);
                if (titleBlockPath is not null)
                {
                    doc.LoadFamily(titleBlockPath, out _);
                    titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
                }
            }

            if (titleBlockId is null)
                return;

            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = SheetNumber;
            sheet.Name = "СК_Аудит_лист";
            ScheduleSheetInstance.Create(
                doc, sheet.Id, sheetSchedule.Id, new XYZ(0.1, 0.1, 0));
            _sheetCreated = true;
        });

        if (_wallIds.Count < 3)
            Skip.Test("Не удалось создать 3 стены в шаблоне проекта");

        _seeded = true;

        // Family loading must happen outside any transaction (LoadFamily on a
        // modifiable document throws), see the verified API traps.
        SeedGroupFamilies(definitions);
    }

    [After(Test)]
    public void Close()
    {
        foreach (var seed in _seeds)
            seed.Dispose();

        _seeds.Clear();
        _sharedFile?.Dispose();
        _document?.Close(false);
    }

    [Test]
    public async Task GetScheduleDefinition_SharedFieldsCarryGuidAndPlacement()
    {
        if (!_seeded)
            Skip.Test("Сид не собрался");

        var tool = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + _sheetScheduleId + "}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("placedOnSheet").GetBoolean()).IsTrue();
        var sheets = data.GetProperty("sheetNumbers");
        await Assert.That(sheets.GetArrayLength()).IsGreaterThan(0);
        await Assert.That(sheets[0].GetString()).IsEqualTo(SheetNumber);

        var fields = data.GetProperty("fields").EnumerateArray().ToList();
        var nameField = fields.First(field =>
            field.TryGetProperty("guid", out var guid)
            && guid.ValueKind == JsonValueKind.String
            && guid.GetString() == NameGuid.ToString("D"));
        await Assert.That(nameField.GetProperty("parameterName").GetString())
            .IsEqualTo(NameParameter);

        var markField = fields.First(field =>
            field.GetProperty("name").GetString()!.Contains("Марка")
            || field.GetProperty("name").GetString()!.Contains("Mark"));
        // A built-in field has no shared GUID — with or without the null key.
        var markHasGuid = markField.TryGetProperty("guid", out var markGuid)
                          && markGuid.ValueKind == JsonValueKind.String;
        await Assert.That(markHasGuid).IsFalse();
        await Assert.That(markField.GetProperty("parameterName").GetString())
            .IsNotEqualTo(string.Empty);

        var bait = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + _baitScheduleId + "}"),
            CancellationToken.None);
        await Assert.That(bait.Success).IsTrue();
        await Assert.That(bait.Data!.Value.GetProperty("placedOnSheet").GetBoolean()).IsFalse();
        await Assert.That(bait.Data.Value.GetProperty("sheetNumbers").GetArrayLength())
            .IsEqualTo(0);
    }

    [Test]
    public async Task ListSchedules_ReportsPlacedOnSheetFlag()
    {
        if (!_seeded)
            Skip.Test("Сид не собрался");

        var tool = new ListSchedulesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"nameContains\":\"СК_\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var schedules = result.Data!.Value.GetProperty("schedules").EnumerateArray().ToList();
        var sheetCard = schedules.First(card =>
            card.GetProperty("scheduleId").GetInt64() == _sheetScheduleId);
        var baitCard = schedules.First(card =>
            card.GetProperty("scheduleId").GetInt64() == _baitScheduleId);
        await Assert.That(sheetCard.GetProperty("placedOnSheet").GetBoolean()).IsTrue();
        await Assert.That(baitCard.GetProperty("placedOnSheet").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task AuditFillRate_AutoMode_UsesOnSheetSchedulesAndBuckets()
    {
        if (!_seeded)
            Skip.Test("Сид не собрался");
        if (!_sheetCreated)
            Skip.Test("В шаблоне нет Title Block — листовая спека не размещена");

        var tool = new AuditFillRateTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("mode").GetString()).IsEqualTo("onSheetSchedules");
        await Assert.That(data.GetProperty("scannedElements").GetInt32()).IsGreaterThanOrEqualTo(3);

        var parameters = data.GetProperty("parameters").EnumerateArray().ToList();
        var names = parameters.Select(p => p.GetProperty("parameterName").GetString()).ToList();
        await Assert.That(names.Contains(NameParameter)).IsTrue();
        await Assert.That(names.Contains(UnitParameter)).IsTrue();
        await Assert.That(names.Contains(BaitParameter)).IsFalse();

        var name = parameters.First(p => p.GetProperty("parameterName").GetString() == NameParameter);
        await Assert.That(name.GetProperty("guid").GetString()).IsEqualTo(NameGuid.ToString("D"));
        await Assert.That(name.GetProperty("filled").GetInt32()).IsEqualTo(2);
        await Assert.That(name.GetProperty("emptyString").GetInt32()).IsEqualTo(1);
        await Assert.That(name.GetProperty("emptyNoValue").GetInt32()).IsEqualTo(0);
        await Assert.That(name.GetProperty("missingParameter").GetInt32()).IsEqualTo(0);
        await Assert.That(name.GetProperty("total").GetInt32()).IsEqualTo(3);
        var culprits = name.GetProperty("culprits");
        await Assert.That(culprits.GetArrayLength()).IsGreaterThan(0);

        var unit = parameters.First(p => p.GetProperty("parameterName").GetString() == UnitParameter);
        await Assert.That(unit.GetProperty("filled").GetInt32()).IsEqualTo(1);
        await Assert.That(unit.GetProperty("emptyNoValue").GetInt32()).IsEqualTo(1);
        await Assert.That(unit.GetProperty("emptyString").GetInt32()).IsEqualTo(1);

        var sources = data.GetProperty("sourceSchedules").EnumerateArray().ToList();
        await Assert.That(sources.Count).IsEqualTo(1);
        await Assert.That(sources[0].GetProperty("name").GetString()).IsEqualTo(SheetScheduleName);

        var offSheet = data.GetProperty("offSheetSchedules").EnumerateArray().ToList();
        var offNames = offSheet.Select(item => item.GetProperty("name").GetString()).ToList();
        await Assert.That(offNames.Contains(BaitScheduleName)).IsTrue();
    }

    [Test]
    public async Task AuditFillRate_ExplicitScheduleIds_IncludeBait()
    {
        if (!_seeded)
            Skip.Test("Сид не собрался");

        var tool = new AuditFillRateTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleIds\":[" + _baitScheduleId + "]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("mode").GetString()).IsEqualTo("scheduleIds");
        var names = data.GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("parameterName").GetString())
            .ToList();
        await Assert.That(names.Contains(BaitParameter)).IsTrue();
        await Assert.That(names.Contains(NameParameter)).IsFalse();
    }

    [Test]
    public async Task GroupFamiliesBySharedParameters_SeparatesFopGroups()
    {
        if (!_familiesSeeded)
            Skip.Test("Сид-семейства не собрались (нет .rft шаблона или ФОП)");

        var tool = new GroupFamiliesBySharedParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scope\":\"type\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("groupCount").GetInt32()).IsGreaterThanOrEqualTo(3);
        var familiesScanned = data.GetProperty("familiesScanned").GetInt32();
        var typesScanned = data.GetProperty("typesScanned").GetInt32();
        await Assert.That(familiesScanned).IsGreaterThanOrEqualTo(2);
        // familiesScanned counts distinct families, typesScanned counts their symbols.
        await Assert.That(familiesScanned).IsLessThanOrEqualTo(typesScanned);

        var groups = data.GetProperty("groups").EnumerateArray().ToList();
        var groupA = groups.First(group =>
            group.GetProperty("guids").GetArrayLength() == 1
            && group.GetProperty("guids")[0].GetString() == GroupAGuid.ToString("D"));
        var groupB = groups.First(group =>
            group.GetProperty("guids").GetArrayLength() == 1
            && group.GetProperty("guids")[0].GetString() == GroupBGuid.ToString("D"));

        var familiesA = groupA.GetProperty("families").EnumerateArray()
            .Select(f => f.GetProperty("familyName").GetString())
            .ToList();
        await Assert.That(familiesA.Contains(FamilyA)).IsTrue();
        var memberA = groupA.GetProperty("families").EnumerateArray()
            .First(f => f.GetProperty("familyName").GetString() == FamilyA);
        await Assert.That(memberA.GetProperty("placedInstanceCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);

        var familiesB = groupB.GetProperty("families").EnumerateArray()
            .Select(f => f.GetProperty("familyName").GetString())
            .ToList();
        await Assert.That(familiesB.Contains(FamilyB)).IsTrue();

        if (data.TryGetProperty("unplacedFamilies", out var unplaced))
        {
            var unplacedNames = unplaced.EnumerateArray()
                .Select(f => f.GetProperty("familyName").GetString())
                .ToList();
            await Assert.That(unplacedNames.Contains(FamilyB)).IsTrue();
            await Assert.That(unplacedNames.Contains(FamilyA)).IsFalse();
        }

        var parametersA = groupA.GetProperty("parameters").EnumerateArray().ToList();
        await Assert.That(parametersA.Count).IsEqualTo(1);
        await Assert.That(parametersA[0].GetProperty("guid").GetString())
            .IsEqualTo(GroupAGuid.ToString("D"));
    }

    [Test]
    public async Task ListTypeParameterCandidates_RecommendsEmptyIdentityDataStrings()
    {
        if (!_seeded)
            Skip.Test("Сид не собрался");

        var tool = new ListTypeParameterCandidatesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"categories\":[\"OST_Walls\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("typesScanned").GetInt32()).IsGreaterThan(0);

        var candidates = data.GetProperty("candidates").EnumerateArray().ToList();
        var typeComments = candidates.First(c =>
            c.GetProperty("builtInParameter").GetString() == "ALL_MODEL_TYPE_COMMENTS");
        await Assert.That(typeComments.GetProperty("group").GetString())
            .IsEqualTo("PG_IDENTITY_DATA");
        await Assert.That(typeComments.GetProperty("storageType").GetString()).IsEqualTo("String");
        await Assert.That(typeComments.GetProperty("isReadOnly").GetBoolean()).IsFalse();
        await Assert.That(typeComments.GetProperty("recommended").GetBoolean()).IsTrue();
        await Assert.That(typeComments.GetProperty("emptyAcrossAllTypes").GetBoolean()).IsTrue();

        var typeName = candidates.FirstOrDefault(c =>
            c.GetProperty("builtInParameter").GetString() == "ALL_MODEL_TYPE_NAME");
        if (typeName.ValueKind != JsonValueKind.Undefined)
            await Assert.That(typeName.GetProperty("isReadOnly").GetBoolean()).IsTrue();

        var invalid = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);
        await Assert.That(invalid.Success).IsFalse();
        await Assert.That(invalid.ErrorCode).IsEqualTo("invalid_arguments");
    }

    private static ElementId SharedId(Document doc, ExternalDefinition definition) =>
        SharedParameterElement.Lookup(doc, definition.GUID)!.Id;

    private Wall Wall(int index) =>
        (Wall)Doc.GetElement(ElementIdCompat.Create(_wallIds[index]));

    private void SetWallValue(int index, ExternalDefinition definition, string value)
    {
        var wall = Doc.GetElement(ElementIdCompat.Create(_wallIds[index]));
        wall?.get_Parameter(definition.GUID)?.Set(value);
    }

    private void BindSharedParameter(Document doc, ExternalDefinition definition)
    {
        var app = doc.Application;
        var category = Category.GetCategory(doc, BuiltInCategory.OST_Walls);
        var categorySet = app.Create.NewCategorySet();
        categorySet.Insert(category);
        var binding = app.Create.NewInstanceBinding(categorySet);
        if (!doc.ParameterBindings.Insert(definition, binding))
            Skip.Test("Не удалось привязать общий параметр «" + definition.Name + "»");
    }

    private (ExternalDefinition Name, ExternalDefinition Unit, ExternalDefinition Bait,
        ExternalDefinition GroupA, ExternalDefinition GroupB) SeedSharedDefinitions()
    {
        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.Create(SharedGroupName);
        var definitions = group.Definitions;
        return (
            CreateDefinition(definitions, NameParameter, NameGuid),
            CreateDefinition(definitions, UnitParameter, UnitGuid),
            CreateDefinition(definitions, BaitParameter, BaitGuid),
            CreateDefinition(definitions, "СК_Группа_А", GroupAGuid),
            CreateDefinition(definitions, "СК_Группа_Б", GroupBGuid));
    }

    private static ExternalDefinition CreateDefinition(
        Definitions definitions,
        string name,
        Guid guid) =>
        (ExternalDefinition)definitions.Create(
            new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
            {
                GUID = guid
            });

    private void SeedGroupFamilies(
        (ExternalDefinition Name, ExternalDefinition Unit, ExternalDefinition Bait,
            ExternalDefinition GroupA, ExternalDefinition GroupB) definitions)
    {
        var seedA = FamilySeed.Create(Application, Doc, FamilyA, out var reasonA);
        if (seedA is null)
            return;

        var seedB = FamilySeed.Create(Application, Doc, FamilyB, out var reasonB);
        if (seedB is null)
        {
            seedA.Dispose();
            return;
        }

        _seeds.Add(seedA);
        _seeds.Add(seedB);
        if (!AddSharedTypeParameter(seedA.FamilyName, definitions.GroupA))
            return;

        if (!AddSharedTypeParameter(seedB.FamilyName, definitions.GroupB))
            return;

        // One placed instance of family A so the unplaced list stays exact.
        _transactions!.RunInTransaction(Doc, "Place family A instance", doc =>
        {
            var family = FindFamily(doc, FamilyA);
            var symbolId = family?.GetFamilySymbolIds().First();
            if (symbolId is null)
                return;

            if (doc.GetElement(symbolId) is FamilySymbol symbol)
            {
                symbol.Activate();
                doc.Create.NewFamilyInstance(
                    new XYZ(5.0, 5.0, 0), symbol, StructuralType.NonStructural);
            }
        });

        _familiesSeeded = true;
    }

    private bool AddSharedTypeParameter(string familyName, ExternalDefinition definition)
    {
        var family = FindFamily(Doc, familyName);
        if (family is null)
            return false;

        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(family);
            var manager = familyDoc.FamilyManager;
            using (var transaction = new Transaction(familyDoc, "Add shared type parameter"))
            {
                transaction.Start();
                manager.AddParameter(definition, GroupTypeId.General, false);
                if (transaction.Commit() != TransactionStatus.Committed)
                    return false;
            }

            familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
                familyDoc.Close(false);
        }
    }

    private static RevitFamily? FindFamily(Document doc, string familyName)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily family &&
                string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return null;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Swaps the shared parameter file to a temp one so the test never touches
    /// the user's ФОП (pattern of AgentMigrationDogfoodTests).
    /// </summary>
    private sealed class SharedFileScope : IDisposable
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _application;
        private readonly string? _previous;
        private readonly string? _temporary;

        private SharedFileScope(
            Autodesk.Revit.ApplicationServices.Application application,
            string temporary)
        {
            _application = application;
            _previous = application.SharedParametersFilename;
            _temporary = temporary;
        }

        public static SharedFileScope Create(
            Autodesk.Revit.ApplicationServices.Application application)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SmartConFillAuditTests");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "shared-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, string.Empty);
            var scope = new SharedFileScope(application, path);
            application.SharedParametersFilename = path;
            return scope;
        }

        public void Dispose()
        {
            try
            {
                _application.SharedParametersFilename = _previous ?? string.Empty;
            }
            catch (Exception)
            {
                // Restoring the shared parameter file is best-effort.
            }

            try
            {
                if (_temporary is not null && File.Exists(_temporary))
                    File.Delete(_temporary);
            }
            catch (Exception)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }
}
