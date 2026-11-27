using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Harness-feedback v16 regressions:
/// D1 — read-back with a whitespace-padded vendor type name (false mismatch);
/// D3 — categoryNames in the family-job selector;
/// D5/C1 — compact + parameterNamePrefix on get_family_parameters;
/// C2 — get_parameter_usage reverse index (schedule field + view filter).
/// </summary>
public sealed class AgentFamiliesV16Tests : RevitApiTest
{
    private Document? _document;
    private FamilySeed? _seed;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Before(Test)]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentV16", out var reason);
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
    public async Task MapValues_WhitespacePaddedTypeName_ReadBackHasNoMismatch()
    {
        // D1 (v16): a vendor family carries a whitespace-padded type name; the
        // read-back used to report "type is missing from the project" for every
        // value though the values DID land. The snapshot now keys types trimmed.
        const string paddedType = "  Тип Пробел  ";
        var family = (RevitFamily)Doc.GetElement(
            SmartCon.Core.Compatibility.ElementIdCompat.Create(Seed.FamilyId))!;
        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(family);
            using (var transaction = new Transaction(familyDoc, "Add padded type"))
            {
                transaction.Start();
                var type = familyDoc.FamilyManager.NewType(paddedType);
                familyDoc.FamilyManager.CurrentType = type;
                transaction.Commit();
            }

            familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
        }
        finally
        {
            try
            {
                familyDoc?.Close(false);
            }
            catch (Exception)
            {
                // The copy is discarded either way.
            }
        }

        // The family element was recreated by the load-back: address by name.
        var add = Run(
            new AddFamilyParameterTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"name\":\"Копия_Марки\"," +
            "\"storageType\":\"text\",\"dryRun\":false}");
        if (!add.Success)
            throw new InvalidOperationException(
                "ADD FAILED: " + add.ErrorCode + " / " + add.Message + " / " +
                (add.Data?.GetRawText() ?? ""));
        await Assert.That(add.Success).IsTrue();

        var map = Run(
            new MapFamilyParametersTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"familyName\":\"" + Seed.FamilyName + "\"," +
            "\"mappings\":[{\"source\":{\"name\":\"" + FamilySeed.CodeParam + "\"}," +
            "\"target\":{\"name\":\"Копия_Марки\"}}],\"dryRun\":false}");

        await Assert.That(map.Success).IsTrue();
        var data = Data(map);
        await Assert.That(data.GetProperty("readBack").GetProperty("mismatched").GetInt32())
            .IsEqualTo(0);
        await Assert.That(data.GetProperty("readBack").GetProperty("verified").GetInt32())
            .IsGreaterThanOrEqualTo(3); // Тип1, Тип2 + the padded type
        await Assert.That(data.GetProperty("failed").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task FamilyJobSelector_CategoryNames_ResolvesLikeSetParameters()
    {
        // D3 (v16): select.categoryNames was rejected by the family-job spec
        // though set_parameters already accepted it.
        var family = (RevitFamily)Doc.GetElement(
            SmartCon.Core.Compatibility.ElementIdCompat.Create(Seed.FamilyId))!;
        var categoryName = family.FamilyCategory!.Name;

        var byName = FamilyJobSelectors.Resolve(
            Doc, null, null, null, null, null, new[] { categoryName }, null);
        await Assert.That(byName.Success).IsTrue();
        await Assert.That(byName.Families.Any(candidate => candidate.Name == Seed.FamilyName))
            .IsTrue();

        var unknown = FamilyJobSelectors.Resolve(
            Doc, null, null, null, null, null, new[] { "OST_NoSuchCategory" }, null);
        await Assert.That(unknown.Success).IsFalse();
        await Assert.That(unknown.ErrorDetail ?? string.Empty).Contains("OST_NoSuchCategory");
    }

    [Test]
    public async Task GetFamilyParameters_CompactAndPrefixFilter_OneCallAudit()
    {
        // D5/C1 (v16): compact=true was dropped as an unknown property and the
        // per-family audit needed unfiltered pages; now compact + prefix are
        // first-class arguments.
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        var compact = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + ",\"compact\":true," +
                  "\"parameterNamePrefix\":\"" + FamilySeed.CodeParam.Substring(0, 2) + "\"}"),
            CancellationToken.None);
        await Assert.That(compact.Success).IsTrue();
        var compactData = Data(compact);
        await Assert.That(compactData.GetProperty("compact").GetBoolean()).IsTrue();
        // Repairs live on the ToolResult envelope, not in Data (validator v16, P1-2).
        await Assert.That(compact.Repairs.Count).IsEqualTo(0);
        // The single-family compact reply must carry SHORT rows, not the full
        // parameter reports with formulas/values (validator v16, P1-2).
        var compactRows = compactData.GetProperty("families");
        await Assert.That(compactRows.GetArrayLength()).IsEqualTo(1);
        var compactParameters = compactRows[0].GetProperty("parameters");
        await Assert.That(compactParameters.GetArrayLength()).IsGreaterThan(0);
        var compactParameter = compactParameters[0];
        await Assert.That(compactParameter.TryGetProperty("name", out _)).IsTrue();
        await Assert.That(compactParameter.TryGetProperty("formula", out _)).IsFalse();
        await Assert.That(compactParameter.TryGetProperty("valuesByType", out _)).IsFalse();
        await Assert.That(compactParameter.TryGetProperty("spec", out _)).IsTrue();
        await Assert.That(compactParameter.EnumerateObject().Count()).IsLessThanOrEqualTo(6);

        var prefix = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId +
                  ",\"parameterNamePrefix\":\"ZGHT_NoSuchPrefix\"}"),
            CancellationToken.None);
        await Assert.That(prefix.Success).IsTrue();
        await Assert.That(Data(prefix).GetProperty("matchedCount").GetInt32()).IsEqualTo(0);

        var exact = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + ",\"parameterNamePrefix\":\"" +
                  FamilySeed.CodeParam + "\"}"),
            CancellationToken.None);
        await Assert.That(exact.Success).IsTrue();
        await Assert.That(Data(exact).GetProperty("matchedCount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task GetParameterUsage_ReportsScheduleFieldAndViewFilter()
    {
        // C2 (v16): the reverse index must answer "which schedules and filters
        // use this parameter" in ONE call.
        const string parameterName = "SC_Usage_Probe";
        using var sharedFile = SharedParameterFileScope.Create(Application);
        var group = Application.OpenSharedParameterFile()!.Groups.Create("SC_Usage");
        var definition = (ExternalDefinition)group.Definitions.Create(
            new ExternalDefinitionCreationOptions(parameterName, SpecTypeId.String.Text)
            {
                GUID = Guid.Parse("9B2D5F71-3A48-4C6E-B07D-1F8A2C4E6D90")
            });

        ElementId? sharedElementId = null;
        ElementId? scheduleId = null;
        ElementId? filterId = null;
        using (var transaction = new Transaction(Doc, "Seed usage probe"))
        {
            transaction.Start();
            var category = Category.GetCategory(Doc, BuiltInCategory.OST_PipeCurves)!;
            var categories = Application.Create.NewCategorySet();
            categories.Insert(category);
            var binding = Application.Create.NewInstanceBinding(categories);
            await Assert.That(Doc.ParameterBindings.Insert(definition, binding)).IsTrue();

            foreach (var element in new FilteredElementCollector(Doc)
                         .OfClass(typeof(ParameterElement)))
            {
                if (element is SharedParameterElement shared
                    && shared.GuidValue == definition.GUID)
                {
                    sharedElementId = shared.Id;
                    break;
                }
            }

            await Assert.That(sharedElementId).IsNotNull();

            var schedule = ViewSchedule.CreateSchedule(Doc, category.Id);
            schedule.Definition.AddField(ScheduleFieldType.Instance, sharedElementId!);
            scheduleId = schedule.Id;

            var rule = ParameterFilterRuleFactory.CreateEqualsRule(sharedElementId!, "probe");
            var elementFilter = new ElementParameterFilter(rule);
            var viewFilter = ParameterFilterElement.Create(
                Doc, "SC_Usage_Filter", new[] { category.Id }, elementFilter);
            filterId = viewFilter.Id;
            transaction.Commit();
        }

        var tool = new GetParameterUsageTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"names\":[\"" + parameterName + "\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var row = Data(result).GetProperty("parameters")[0];
        await Assert.That(row.GetProperty("notFound").ValueKind).IsEqualTo(JsonValueKind.Undefined)
            .Or.IsEqualTo(JsonValueKind.Null);
        await Assert.That(row.GetProperty("scheduleCount").GetInt32()).IsGreaterThanOrEqualTo(1);
        var scheduleIds = row.GetProperty("schedules").EnumerateArray()
            .Select(entry => entry.GetProperty("scheduleId").GetInt64()).ToList();
        await Assert.That(scheduleIds.Contains(scheduleId!.GetValue())).IsTrue();
        await Assert.That(row.GetProperty("filterCount").GetInt32()).IsGreaterThanOrEqualTo(1);
        var filterIds = row.GetProperty("filters").EnumerateArray()
            .Select(entry => entry.GetProperty("filterId").GetInt64()).ToList();
        await Assert.That(filterIds.Contains(filterId!.GetValue())).IsTrue();

        // A prefix sweep covers the same definition in one call.
        var prefix = tool.ExecuteForTests(
            Doc, Parse("{\"namePrefix\":\"SC_Usage_\"}"), CancellationToken.None);
        await Assert.That(prefix.Success).IsTrue();
        await Assert.That(Data(prefix).GetProperty("parameterCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(Data(prefix).GetProperty("usedAnywhereCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);

        // An unknown name is an honest notFound, not an empty silent success.
        var missing = tool.ExecuteForTests(
            Doc, Parse("{\"names\":[\"SC_Usage_NoSuch\"]}"), CancellationToken.None);
        await Assert.That(missing.Success).IsTrue();
        await Assert.That(Data(missing).GetProperty("parameters")[0]
            .GetProperty("notFound").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task VerifyFamilyScope_Paging_NextOffsetHasNoGaps()
    {
        // P1-1 (validator v16): nextVerificationOffset double-counted the
        // offset prefix, so the second page silently skipped a batch and the
        // pass looked complete.
        var seeds = new List<FamilySeed> { Seed };
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var extra = FamilySeed.Create(
                    Application, Doc, "SC_AgentV16_Page" + index, out var reason);
                if (extra is null)
                {
                    Skip.Test(reason ?? "Не удалось сидировать семейство для пейджинга");
                    return;
                }

                seeds.Add(extra);
            }

            // A project binding alone: the parameter is absent from every
            // family document, so all three candidates need verification.
            const string parameterName = "SC_Paging_Probe";
            using var sharedFile = SharedParameterFileScope.Create(Application);
            var group = Application.OpenSharedParameterFile()!.Groups.Create("SC_V16Page");
            var definition = (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions(parameterName, SpecTypeId.String.Text)
                {
                    GUID = Guid.Parse("4C7E1A02-9B35-4D68-8E01-2F5A7B9C0D31")
                });

            using (var transaction = new Transaction(Doc, "Bind paging probe"))
            {
                transaction.Start();
                var family = (RevitFamily)Doc.GetElement(
                    SmartCon.Core.Compatibility.ElementIdCompat.Create(Seed.FamilyId))!;
                var categories = Application.Create.NewCategorySet();
                categories.Insert(family.FamilyCategory);
                // A TYPE binding: instance parameters are out of the default
                // type-level scan (includeInstanceParameters=false).
                var binding = Application.Create.NewTypeBinding(categories);
                await Assert.That(Doc.ParameterBindings.Insert(definition, binding)).IsTrue();
                transaction.Commit();
            }

            var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));
            var first = tool.ExecuteForTests(
                Doc,
                Parse("{\"parameterName\":\"" + parameterName + "\",\"verifyFamilyScope\":true," +
                      "\"verificationLimit\":1,\"verificationSeconds\":150,\"summary\":true}"),
                CancellationToken.None);
            await Assert.That(first.Success).IsTrue();
            // v17 (1.2/1.3): the per-name inventory says WHAT was checked —
            // "verifiedFamilies: 0" must never read as "absent".
            var firstName = Data(first).GetProperty("byParameterName")[0];
            await Assert.That(firstName.GetProperty("familiesFromProjectBindingOnly")
                .GetInt32()).IsGreaterThanOrEqualTo(1);
            await Assert.That(firstName.GetProperty("familiesNotChecked").GetInt32())
                .IsGreaterThanOrEqualTo(2);
            await Assert.That(Data(first).GetProperty("verificationRemainingCandidates")
                .GetInt32()).IsGreaterThanOrEqualTo(2);
            var firstStats = Data(first).GetProperty("scopeVerification");
            await Assert.That(firstStats.GetProperty("candidates").GetInt32())
                .IsGreaterThanOrEqualTo(3);
            await Assert.That(firstStats.GetProperty("verified").GetInt32()).IsEqualTo(1);
            await Assert.That(firstStats.GetProperty("truncated").GetBoolean()).IsTrue();
            var next = firstStats.GetProperty("nextVerificationOffset").GetInt32();
            await Assert.That(next).IsEqualTo(1);
            // v17 (1.2): the source of a matched name is explicit.
            var firstRow = Data(first).GetProperty("families")[0];
            await Assert.That(firstRow.GetProperty("sources").GetProperty(parameterName)
                .GetString()).IsNotNull();

            var second = tool.ExecuteForTests(
                Doc,
                Parse("{\"parameterName\":\"" + parameterName + "\",\"verifyFamilyScope\":true," +
                      "\"verificationLimit\":1,\"verificationOffset\":" + next +
                      ",\"verificationSeconds\":150}"),
                CancellationToken.None);
            await Assert.That(second.Success).IsTrue();
            var secondStats = Data(second).GetProperty("scopeVerification");
            await Assert.That(secondStats.GetProperty("verified").GetInt32()).IsEqualTo(1);
            // 1 skipped by offset + 1 verified — NOT 1 + 1 + offset again.
            await Assert.That(secondStats.GetProperty("nextVerificationOffset").GetInt32())
                .IsEqualTo(2);

            var beyond = tool.ExecuteForTests(
                Doc,
                Parse("{\"parameterName\":\"" + parameterName + "\",\"verifyFamilyScope\":true," +
                      "\"verificationLimit\":1,\"verificationOffset\":500," +
                      "\"verificationSeconds\":150}"),
                CancellationToken.None);
            await Assert.That(beyond.Success).IsTrue();
            // Warnings of this tool live in the payload, not on the envelope.
            var beyondWarnings = Data(beyond).GetProperty("warnings").EnumerateArray()
                .Select(warning => warning.GetString() ?? string.Empty)
                .ToList();
            await Assert.That(beyondWarnings.Any(warning =>
                    warning.Contains("beyond", StringComparison.OrdinalIgnoreCase)))
                .IsTrue();
        }
        finally
        {
            foreach (var seed in seeds)
                seed.Dispose();
        }
    }

    [Test]
    public async Task ListSharedParameters_Path_ReadsFileWithoutSwitchingActive()
    {
        // v17 (1.4): compare two ФОП standards without touching the user's
        // active file — parsed as text, filePath echoes the path.
        var path = Path.Combine(
            Path.GetTempPath(), "sc-fop-alt-" + Guid.NewGuid().ToString("N") + ".txt");
        var lines = new[]
        {
            "*META\tVERSION\tMINVERSION",
            "META\t2\t1",
            "*GROUP\tID\tNAME",
            "GROUP\t1\tSC_Alt",
            "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE",
            "PARAM\t5e0d1a44-39fa-4c1e-8f21-4d1a5f33a101\tSC_Alt_Парам\tTEXT\t\t1\t1\t\t1",
            "PARAM\t5e0d1a44-39fa-4c1e-8f21-4d1a5f33a102\tSC_Alt_Второй\tLENGTH\t\t1\t1\t\t1"
        };
        await File.WriteAllTextAsync(path, string.Join("\n", lines));
        try
        {
            var tool = new ListSharedParametersTool(new StubRevitContext(Doc));
            var result = tool.ExecuteForTests(
                Doc,
                Parse("{\"path\":\"" + path.Replace("\\", "\\\\") + "\"}"),
                CancellationToken.None);

            await Assert.That(result.Success).IsTrue();
            var data = Data(result);
            await Assert.That(data.GetProperty("filePath").GetString()).IsEqualTo(path);
            await Assert.That(data.GetProperty("parsedFromText").GetBoolean()).IsTrue();
            await Assert.That(data.GetProperty("groupsCount").GetInt32()).IsEqualTo(1);
            await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(2);
            var names = data.GetProperty("parameters").EnumerateArray()
                .Select(row => row.GetProperty("name").GetString())
                .ToList();
            await Assert.That(names.Contains("SC_Alt_Парам")).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private ToolResult Run(AgentWriteToolBase tool, string json) =>
        tool.ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");
}
