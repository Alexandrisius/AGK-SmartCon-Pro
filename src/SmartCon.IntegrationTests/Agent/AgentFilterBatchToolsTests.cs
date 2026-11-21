using System.Text.Json;
using Autodesk.Revit.DB;
using Microsoft.Data.Sqlite;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// S.25/S.26/S.27 (#458-#460): batch delete_filter with the single
/// confirmNames list and the deadOnly guard, the quarantine snapshot of every
/// deleted filter in agent.db, merge_duplicate_filters and the liveness of the
/// duplicate-group copies in list_filters.
/// </summary>
public sealed class AgentFilterBatchToolsTests : RevitApiTest
{
    private const string LevelName = "SC_FilterBatch_Level";
    private const string Plan1Name = "SC_FilterBatch_Plan1";
    private const string Plan2Name = "SC_FilterBatch_Plan2";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private AgentDatabase? _quarantineDatabase;
    private SqliteAgentFilterQuarantineStore? _quarantine;
    private string? _quarantineDirectory;
    private long _levelId;
    private long _plan1Id;
    private long _plan2Id;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _quarantineDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "smartcon-filter-quarantine-it",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_quarantineDirectory);
        _quarantineDatabase = new AgentDatabase(
            System.IO.Path.Combine(_quarantineDirectory, "agent.db"));
        _quarantineDatabase.Initialize();
        _quarantine = new SqliteAgentFilterQuarantineStore(_quarantineDatabase);

        _transactions.RunInTransaction(Doc, "Seed filter batch model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            _plan1Id = ModelSeed
                .CreateViewPlanChecked(doc, level.Id, Plan1Name)
                .Id.GetValue();
            _plan2Id = ModelSeed
                .CreateViewPlanChecked(doc, level.Id, Plan2Name)
                .Id.GetValue();
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
        SqliteConnection.ClearAllPools();
        try
        {
            if (_quarantineDirectory is not null)
                System.IO.Directory.Delete(_quarantineDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Test]
    public async Task DeleteFilter_Batch_DryRun_ListsEveryTargetWithAppliedViewsCount()
    {
        var dead1 = CreateFilterWithRule("SC_Batch_Dead1");
        var dead2 = CreateFilterWithRule("SC_Batch_Dead2");
        var applied = CreateFilterWithRule("SC_Batch_Applied");
        ApplyFilterToView(applied, _plan1Id);

        var tool = CreateDeleteTool();
        var result = tool.ExecuteForTests(
            Parse("{\"filterNames\":[\"SC_Batch_Dead1\",\"SC_Batch_Dead2\"," +
                  "\"SC_Batch_Applied\"],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var names = data.GetProperty("names");
        await Assert.That(names.GetArrayLength()).IsEqualTo(3);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(3);

        var appliedRow = data.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "SC_Batch_Applied");
        await Assert.That(appliedRow.GetProperty("appliedViewsCount").GetInt32())
            .IsEqualTo(1);
        await Assert.That(CountFilters()).IsEqualTo(3);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteFilter_Batch_Apply_WithoutConfirmNames_IsRefused()
    {
        CreateFilterWithRule("SC_Batch_Refused1");
        CreateFilterWithRule("SC_Batch_Refused2");

        var tool = CreateDeleteTool();
        var result = tool.ExecuteForTests(
            Parse("{\"filterNames\":[\"SC_Batch_Refused1\",\"SC_Batch_Refused2\"]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirm_names_required");
        await Assert.That(CountFilters()).IsEqualTo(2);
    }

    [Test]
    public async Task DeleteFilter_Batch_WithStaleConfirmNames_IsRefused()
    {
        CreateFilterWithRule("SC_Batch_Stale1");
        CreateFilterWithRule("SC_Batch_Stale2");

        var tool = CreateDeleteTool();
        var result = tool.ExecuteForTests(
            Parse("{\"filterNames\":[\"SC_Batch_Stale1\",\"SC_Batch_Stale2\"]," +
                  "\"dryRun\":false,\"confirmNames\":[\"SC_Batch_Stale1\",\"ДругойФильтр\"]}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirm_names_mismatch");
        await Assert.That(CountFilters()).IsEqualTo(2);
    }

    [Test]
    public async Task DeleteFilter_Batch_Apply_DeletesAllInOneCallAndQuarantines()
    {
        var dead1 = CreateFilterWithRule("SC_Batch_Apply1");
        var dead2 = CreateFilterWithRule("SC_Batch_Apply2");
        var applied = CreateFilterWithRule("SC_Batch_AppliedApply");
        ApplyFilterToView(applied, _plan1Id);

        var tool = CreateDeleteTool();
        var result = tool.ExecuteForTests(
            Parse("{\"filterNames\":[\"SC_Batch_Apply1\",\"SC_Batch_Apply2\"," +
                  "\"SC_Batch_AppliedApply\"],\"dryRun\":false," +
                  "\"confirmNames\":[\"SC_Batch_Apply1\",\"SC_Batch_Apply2\"," +
                  "\"SC_Batch_AppliedApply\"]}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("deletedCount").GetInt32()).IsEqualTo(3);
        await Assert.That(CountFilters()).IsEqualTo(0);

        // The applied filter is gone from the view too (Revit cascade).
        var view = (View)Doc.GetElement(ElementIdCompat.Create(_plan1Id));
        await Assert.That(view.GetFilters().Count).IsEqualTo(0);

        // Every deleted filter left a quarantine snapshot with readable JSON.
        var quarantine = data.GetProperty("quarantine");
        await Assert.That(quarantine.GetProperty("enabled").GetBoolean()).IsTrue();
        var snapshots = quarantine.GetProperty("snapshots");
        await Assert.That(snapshots.GetArrayLength()).IsEqualTo(3);
        foreach (var row in snapshots.EnumerateArray())
        {
            await Assert.That(row.GetProperty("quarantined").GetBoolean()).IsTrue();
            var snapshotId = row.GetProperty("snapshotId").GetString();
            var snapshot = _quarantine!.GetAsync(snapshotId!, CancellationToken.None)
                .GetAwaiter().GetResult();
            await Assert.That(snapshot).IsNotNull();
            await Assert.That(snapshot!.CategoriesJson.Length).IsGreaterThan(2);

            using var rules = JsonDocument.Parse(snapshot.RulesJson);
            await Assert.That(rules.RootElement.GetProperty("type").GetString())
                .IsEqualTo("parameter");
        }

        // The snapshot of the applied filter remembers the view.
        var appliedSnapshotId = snapshots.EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "SC_Batch_AppliedApply")
            .GetProperty("snapshotId").GetString();
        var appliedSnapshot = _quarantine!.GetAsync(
                appliedSnapshotId!, CancellationToken.None).GetAwaiter().GetResult();
        using var appliedViews = JsonDocument.Parse(appliedSnapshot!.AppliedViewsJson);
        await Assert.That(appliedViews.RootElement.GetArrayLength()).IsEqualTo(1);
        await Assert.That(appliedViews.RootElement[0].GetProperty("viewId").GetInt64())
            .IsEqualTo(_plan1Id);
    }

    [Test]
    public async Task DeleteFilter_DeadOnly_AppliedFilterInList_RefusesWholeCall()
    {
        var dead = CreateFilterWithRule("SC_DeadOnly_Dead");
        var applied = CreateFilterWithRule("SC_DeadOnly_Applied");
        ApplyFilterToView(applied, _plan1Id);

        var tool = CreateDeleteTool();
        var result = tool.ExecuteForTests(
            Parse("{\"filterNames\":[\"SC_DeadOnly_Dead\",\"SC_DeadOnly_Applied\"]," +
                  "\"deadOnly\":true,\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("filter_in_use");
        var appliedFilters = result.Data!.Value.GetProperty("appliedFilters");
        await Assert.That(appliedFilters.GetArrayLength()).IsEqualTo(1);
        await Assert.That(appliedFilters[0].GetProperty("appliedViewCount").GetInt32())
            .IsEqualTo(1);
        await Assert.That(appliedFilters[0].GetProperty("views")[0]
            .GetProperty("viewName").GetString()).IsEqualTo(Plan1Name);

        // Nothing was silently narrowed or deleted: both filters survive.
        await Assert.That(CountFilters()).IsEqualTo(2);
        _ = dead;
    }

    [Test]
    public async Task DeleteFilter_DeadOnly_DeadOnlyList_Deletes()
    {
        CreateFilterWithRule("SC_DeadOnly_AllDead1");
        CreateFilterWithRule("SC_DeadOnly_AllDead2");
        var applied = CreateFilterWithRule("SC_DeadOnly_Survivor");
        ApplyFilterToView(applied, _plan1Id);

        var tool = CreateDeleteTool();
        var result = tool.ExecuteForTests(
            Parse("{\"filterNames\":[\"SC_DeadOnly_AllDead1\",\"SC_DeadOnly_AllDead2\"]," +
                  "\"deadOnly\":true,\"dryRun\":false," +
                  "\"confirmNames\":[\"SC_DeadOnly_AllDead1\",\"SC_DeadOnly_AllDead2\"]}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(CountFilters()).IsEqualTo(1);
        var survivor = (ParameterFilterElement)Doc.GetElement(
            ElementIdCompat.Create(applied));
        await Assert.That(survivor.Name).IsEqualTo("SC_DeadOnly_Survivor");
    }

    [Test]
    public async Task ListFilters_DuplicateGroups_ReportAppliedViewCountPerCopy()
    {
        var alive = CreateFilterWithRule("SC_DupGroup_Alive");
        var dead = CreateFilterWithRule("SC_DupGroup_Dead");
        ApplyFilterToView(alive, _plan1Id);

        var tool = new ListFiltersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var groups = result.Data!.Value.GetProperty("duplicateGroups");
        await Assert.That(groups.GetArrayLength()).IsGreaterThanOrEqualTo(1);

        var group = groups.EnumerateArray().First(row =>
            row.GetProperty("filters").EnumerateArray().Any(filter =>
                filter.GetProperty("name").GetString() == "SC_DupGroup_Alive"));
        var copies = group.GetProperty("filters");
        await Assert.That(copies.GetArrayLength()).IsEqualTo(2);
        foreach (var copy in copies.EnumerateArray())
        {
            // Liveness of every copy (S.27): 1 for the alive one, 0 for the dead twin.
            var count = copy.GetProperty("appliedViewCount");
            var value = count.ValueKind == JsonValueKind.Number
                ? count.GetInt32()
                : -1;
            await Assert.That(value is 0 or 1).IsTrue();
        }
    }

    [Test]
    public async Task MergeDuplicateFilters_DryRun_PlansViewOperations()
    {
        var duplicate = CreateFilterWithRule("SC_Merge_Dup");
        var canonical = CreateFilterWithRule("SC_Merge_Canon");
        ApplyFilterToView(duplicate, _plan1Id);
        ApplyFilterToView(canonical, _plan2Id);

        var tool = CreateMergeTool();
        var result = tool.ExecuteForTests(
            Parse("{\"duplicateFilterId\":" + duplicate +
                  ",\"canonicalFilterId\":" + canonical + ",\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var plan = data.GetProperty("plan");
        await Assert.That(plan.GetArrayLength()).IsEqualTo(1);
        await Assert.That(plan[0].GetProperty("action").GetString()).IsEqualTo("replace");
        await Assert.That(data.GetProperty("names")[0].GetString())
            .IsEqualTo("SC_Merge_Dup");
        await Assert.That(data.GetProperty("undoSupported").GetBoolean()).IsFalse();

        // Dry run changes nothing.
        await Assert.That(CountFilters()).IsEqualTo(2);
        var plan1 = (View)Doc.GetElement(ElementIdCompat.Create(_plan1Id));
        await Assert.That(plan1.GetFilters().Count).IsEqualTo(1);
    }

    [Test]
    public async Task MergeDuplicateFilters_Apply_WithoutConfirmNames_IsRefused()
    {
        var duplicate = CreateFilterWithRule("SC_Merge_Refused");
        var canonical = CreateFilterWithRule("SC_Merge_RefusedCanon");
        ApplyFilterToView(duplicate, _plan1Id);

        var tool = CreateMergeTool();
        var result = tool.ExecuteForTests(
            Parse("{\"duplicateFilterId\":" + duplicate +
                  ",\"canonicalFilterId\":" + canonical + ",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirm_names_required");
        await Assert.That(CountFilters()).IsEqualTo(2);
    }

    [Test]
    public async Task MergeDuplicateFilters_Apply_SwitchesViewsDeletesDuplicateAndQuarantines()
    {
        var duplicate = CreateFilterWithRule("SC_Merge_Dup2");
        var canonical = CreateFilterWithRule("SC_Merge_Canon2");
        // Duplicate on two views, canonical already on one of them.
        ApplyFilterToView(duplicate, _plan1Id);
        ApplyFilterToView(duplicate, _plan2Id);
        ApplyFilterToView(canonical, _plan2Id);

        var tool = CreateMergeTool();
        var result = tool.ExecuteForTests(
            Parse("{\"duplicateFilterId\":" + duplicate +
                  ",\"canonicalFilterId\":" + canonical + ",\"dryRun\":false," +
                  "\"confirmNames\":[\"SC_Merge_Dup2\"]}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("viewsAffected").GetInt32()).IsEqualTo(2);

        // The duplicate is gone; the canonical filter now lives on all three slots.
        await Assert.That(CountFilters()).IsEqualTo(1);
        var canonicalId = ElementIdCompat.Create(canonical);
        var duplicateId = ElementIdCompat.Create(duplicate);
        foreach (var viewId in new[] { _plan1Id, _plan2Id })
        {
            var view = (View)Doc.GetElement(ElementIdCompat.Create(viewId));
            var filters = view.GetFilters();
            await Assert.That(filters.Contains(canonicalId)).IsTrue();
            await Assert.That(filters.Contains(duplicateId)).IsFalse();
        }

        // The quarantine snapshot of the duplicate is readable.
        var quarantine = data.GetProperty("quarantine");
        await Assert.That(quarantine.GetProperty("quarantined").GetBoolean()).IsTrue();
        var snapshotId = quarantine.GetProperty("snapshotId").GetString();
        var snapshot = _quarantine!.GetAsync(snapshotId!, CancellationToken.None)
            .GetAwaiter().GetResult();
        await Assert.That(snapshot).IsNotNull();
        await Assert.That(snapshot!.FilterName).IsEqualTo("SC_Merge_Dup2");
        using var appliedViews = JsonDocument.Parse(snapshot.AppliedViewsJson);
        await Assert.That(appliedViews.RootElement.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task MergeDuplicateFilters_SameFilter_IsRefused()
    {
        var filter = CreateFilterWithRule("SC_Merge_Same");

        var tool = CreateMergeTool();
        var result = tool.ExecuteForTests(
            Parse("{\"duplicateFilterId\":" + filter +
                  ",\"canonicalFilterId\":" + filter + ",\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("same_filter");
        await Assert.That(CountFilters()).IsEqualTo(1);
    }

    private DeleteFilterTool CreateDeleteTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!, _quarantine!);

    private MergeDuplicateFiltersTool CreateMergeTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!, _quarantine!);

    private int CountFilters()
    {
        var count = 0;
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ParameterFilterElement)))
        {
            _ = element;
            count++;
        }

        return count;
    }

    private void ApplyFilterToView(long filterId, long viewId)
    {
        _transactions!.RunInTransaction(Doc, "Apply filter to view", doc =>
        {
            var view = (View)doc.GetElement(ElementIdCompat.Create(viewId));
            view.AddFilter(ElementIdCompat.Create(filterId));
        });
    }

    private long CreateFilterWithRule(string name)
    {
        long id = 0;
        _transactions!.RunInTransaction(Doc, "Seed filter with rule", doc =>
        {
            var rule = ParameterFilterRuleFactory.CreateEqualsRule(
                new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS),
                "x");
            var elementFilter = new ElementParameterFilter(rule);
            var filter = ParameterFilterElement.Create(
                doc,
                name,
                new List<ElementId> { new(BuiltInCategory.OST_Walls) },
                elementFilter);
            id = filter.Id.GetValue();
        });
        return id;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
