using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// L1 preflight, L4 dry-run fidelity and L6 read-back against a real document:
/// the plan executor must verify the promised diff, apply only after approval
/// and leave an undoable change set per step.
/// </summary>
public sealed class AgentPlanExecutionTests : RevitApiTest
{
    private const string LevelName = "SC_Plan_Level";
    private const string PlanName = "SC_Plan_Plan";
    private const string TargetName = "SC_Plan_Target";
    private const string MissingViewId = "987654321";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _planId;
    private long _targetId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed agent plan", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            _planId = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName).Id.GetValue();
            _targetId = ModelSeed.CreateViewPlanChecked(doc, level.Id, TargetName).Id.GetValue();
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task Preflight_MissingView_IsRejectedBeforeAnyTransaction()
    {
        var tool = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var context = new AgentToolContext(
            new ImmediateAwaitableEvent(), AgentDocumentIdentity.GetDocumentId(Doc), Guid.Empty);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + MissingViewId + ",\"newName\":\"SC_Plan_Nope\"}]," +
            "\"dryRun\":false}");

        var result = await tool.PreflightAsync(context, args, CancellationToken.None);

        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unresolved_id");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
    }

    [Test]
    public async Task Plan_TwoSteps_DryRunApproveApplyReadBackAndUndo()
    {
        var rename = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var settings = new GetViewSettingsTool(new StubRevitContext(Doc));
        var registry = new TestToolRegistry(new IAgentTool[] { rename, settings });
        var confirmation = new FixedConfirmation();
        var executor = new AgentPlanExecutor(
            registry, new ImmediateAwaitableEvent(), confirmation);

        var plan = new AgentPlan
        {
            Goal = "Rename two eval views",
            Steps = new[]
            {
                RenameStep("s1", _targetId, "SC_Plan_RenamedA"),
                RenameStep("s2", _planId, "SC_Plan_RenamedB")
            }
        };
        var execution = new AgentPlanExecutionContext
        {
            DocumentId = AgentDocumentIdentity.GetDocumentId(Doc)
        };

        var result = await executor.ExecuteAsync(plan, execution, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Approved).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(2);
        await Assert.That(confirmation.Requests.Count).IsEqualTo(1);
        await Assert.That(confirmation.Requests[0].PlanDigest).IsNotNull();
        await Assert.That(result.Steps.All(step => step.FidelityChecked && step.FidelityMatched))
            .IsTrue();

        await Assert.That(GetView(_targetId).Name).IsEqualTo("SC_Plan_RenamedA");
        await Assert.That(GetView(_planId).Name).IsEqualTo("SC_Plan_RenamedB");
        await Assert.That(result.Verified.Count).IsEqualTo(2);
        await Assert.That(result.Mismatches.Count).IsEqualTo(0);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(2);

        var undo = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        await Assert.That(undo.ExecuteForTests(
                Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None).Success)
            .IsTrue();
        await Assert.That(undo.ExecuteForTests(
                Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None).Success)
            .IsTrue();

        await Assert.That(GetView(_targetId).Name).IsEqualTo(TargetName);
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
    }

    [Test]
    public async Task DiscardChangeSet_AfterUndoConflict_NextUndoRestoresPreviousSet()
    {
        var rename = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var first = rename.ExecuteForTests(
            Parse(RenameArgs(_planId, "SC_Plan_SetA")), approved: true, CancellationToken.None);
        await Assert.That(first.Success).IsTrue();
        var firstId = _changeSets!.Last!.Header.Id;

        var second = rename.ExecuteForTests(
            Parse(RenameArgs(_targetId, "SC_Plan_SetB")), approved: true, CancellationToken.None);
        await Assert.That(second.Success).IsTrue();
        var secondId = _changeSets.All.Single(set => set.Header.Id != firstId).Header.Id;

        _transactions!.RunInTransaction(Doc, "Edit the target behind SmartCon's back", doc =>
        {
            var target = (View)doc.GetElement(ElementIdCompat.Create(_targetId));
            target.Name = "SC_Plan_Manual";
        });

        var undo = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var conflict = undo.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);
        await Assert.That(conflict.Success).IsFalse();
        await Assert.That(conflict.ErrorCode).IsEqualTo("undo_conflict");

        var discard = new DiscardChangeSetTool(new StubRevitContext(Doc), _changeSets!);
        var discardContext = new AgentToolContext(
            new ImmediateAwaitableEvent(), AgentDocumentIdentity.GetDocumentId(Doc), Guid.Empty)
        {
            WriteApproved = true
        };
        var discarded = await discard.ExecuteAsync(
            discardContext,
            Parse("{\"changeSetId\":\"" + secondId.ToString("D") + "\",\"dryRun\":false}"),
            CancellationToken.None);

        await Assert.That(discarded.Success).IsTrue();
        var lastApplied = await _changeSets.GetLastAppliedAsync(
            AgentDocumentIdentity.GetDocumentId(Doc), CancellationToken.None);
        await Assert.That(lastApplied!.Header.Id).IsEqualTo(firstId);

        var next = undo.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);
        await Assert.That(next.Success).IsTrue();
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
        await Assert.That(GetView(_targetId).Name).IsEqualTo("SC_Plan_Manual");
    }

    [Test]
    public async Task DiscardChangeSet_WithoutApproval_IsRejected()
    {
        var rename = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var applied = rename.ExecuteForTests(
            Parse(RenameArgs(_planId, "SC_Plan_NoDiscard")), approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();

        var discard = new DiscardChangeSetTool(new StubRevitContext(Doc), _changeSets!);
        var context = new AgentToolContext(
            new ImmediateAwaitableEvent(), AgentDocumentIdentity.GetDocumentId(Doc), Guid.Empty);

        var preview = await discard.ExecuteAsync(context, Parse("{}"), CancellationToken.None);
        await Assert.That(preview.Success).IsTrue();
        await Assert.That(preview.Data!.Value.GetProperty("wouldDiscard").GetBoolean()).IsTrue();

        var result = await discard.ExecuteAsync(
            context, Parse("{\"dryRun\":false}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirmation_required");
        await Assert.That(_changeSets!.Last!.Header.AppliedAt).IsNotNull();
    }

    [Test]
    public async Task DiscardChangeSet_InvalidId_LeavesLastSetUntouched()
    {
        var rename = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var applied = rename.ExecuteForTests(
            Parse(RenameArgs(_planId, "SC_Plan_InvalidDiscard")), approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();
        var lastId = _changeSets!.Last!.Header.Id;

        var discard = new DiscardChangeSetTool(new StubRevitContext(Doc), _changeSets!);
        var context = new AgentToolContext(
            new ImmediateAwaitableEvent(), AgentDocumentIdentity.GetDocumentId(Doc), Guid.Empty)
        {
            WriteApproved = true
        };

        var result = await discard.ExecuteAsync(
            context, Parse("{\"changeSetId\":\"not-a-guid\"}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_change_set_id");
        var lastApplied = await _changeSets.GetLastAppliedAsync(
            AgentDocumentIdentity.GetDocumentId(Doc), CancellationToken.None);
        await Assert.That(lastApplied!.Header.Id).IsEqualTo(lastId);
    }

    [Test]
    public async Task DiscardChangeSet_AlreadyRolledBackSet_IsRejected()
    {
        var rename = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var applied = rename.ExecuteForTests(
            Parse(RenameArgs(_planId, "SC_Plan_RolledBack")), approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();
        var setId = _changeSets!.Last!.Header.Id;

        var undo = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undone = undo.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);
        await Assert.That(undone.Success).IsTrue();

        var discard = new DiscardChangeSetTool(new StubRevitContext(Doc), _changeSets!);
        var context = new AgentToolContext(
            new ImmediateAwaitableEvent(), AgentDocumentIdentity.GetDocumentId(Doc), Guid.Empty)
        {
            WriteApproved = true
        };

        var result = await discard.ExecuteAsync(
            context,
            Parse("{\"changeSetId\":\"" + setId.ToString("D") + "\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("change_set_not_applied");
    }

    [Test]
    public async Task Plan_CreateViewThenCropOnCreated_PreviewSkipsDependentAndApplyRunsBoth()
    {
        const string createdName = "SC_Plan_Created";
        var create = new CreateViewTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var crop = new SetViewCropTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var registry = new TestToolRegistry(new IAgentTool[] { create, crop });
        var executor = new AgentPlanExecutor(
            registry, new ImmediateAwaitableEvent(), new FixedConfirmation());

        var plan = new AgentPlan
        {
            Goal = "Create a view and crop it",
            Steps = new[]
            {
                new AgentPlanStep
                {
                    StepId = "s1",
                    ToolName = "create_view",
                    ArgumentsJson = "{\"kind\":\"plan\",\"levelId\":" + _levelId +
                                    ",\"name\":\"" + createdName + "\",\"dryRun\":false}"
                },
                new AgentPlanStep
                {
                    StepId = "s2",
                    ToolName = "set_view_crop",
                    ArgumentsJson = "{\"select\":{\"nameContains\":\"" + createdName +
                                    "\"},\"crop\":{\"active\":true,\"minMm\":[-5000,-5000]," +
                                    "\"maxMm\":[5000,5000]},\"dryRun\":false}",
                    DependsOn = new[] { "s1" }
                }
            }
        };
        var execution = new AgentPlanExecutionContext
        {
            DocumentId = AgentDocumentIdentity.GetDocumentId(Doc)
        };

        var preview = await executor.PreviewAsync(plan, execution, CancellationToken.None);

        if (!preview.Success)
        {
            var details = string.Join("; ", preview.Steps.Select(step =>
                step.StepId + ":" + step.ToolName + ":" +
                (step.DryRunResult is null
                    ? "no-dry-run"
                    : step.DryRunResult.Success + "/" + step.DryRunResult.ErrorCode + "/" +
                      step.DryRunResult.Message)));
            throw new InvalidOperationException(
                "preview failed: " + preview.ErrorCode + ": " + preview.Message + " steps=" + details);
        }

        await Assert.That(preview.Success).IsTrue();
        await Assert.That(preview.PreviewIncomplete).IsTrue();
        await Assert.That(preview.NotPreviewed.Contains("s2")).IsTrue();
        var skippedStep = preview.Steps.Single(step => step.StepId == "s2");
        await Assert.That(skippedStep.PreviewSkipped).IsTrue();
        await Assert.That(skippedStep.PreviewSkipReason)
            .IsEqualTo("depends_on_unapplied_step:s1(create_view)");
        await Assert.That(FindViewByName(createdName)).IsNull();

        var apply = await executor.ExecuteAsync(plan, execution, CancellationToken.None);

        await Assert.That(apply.Success).IsTrue();
        await Assert.That(apply.PreviewIncomplete).IsTrue();
        var created = FindViewByName(createdName);
        await Assert.That(created).IsNotNull();
        await Assert.That(created!.CropBoxActive).IsTrue();
        var width = created.CropBox!.Max.X - created.CropBox.Min.X;
        await Assert.That(Math.Abs(width - UnitUtils.ConvertToInternalUnits(
                10000, UnitTypeId.Millimeters)) < 0.0033).IsTrue();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(2);
    }

    private View? FindViewByName(string name) =>
        new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => string.Equals(view.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Seeds two view templates from the plan: one with every include on (it
    /// controls "V/G Overrides Filters") and one with the filters include
    /// detached. Both control visibility — the default of a fresh template.
    /// </summary>
    private (long TemplateWithFilters, long TemplateFiltersOff) SeedTemplates()
    {
        long withFilters = 0;
        long filtersOff = 0;
        _transactions!.RunInTransaction(Doc, "Seed plan templates", doc =>
        {
            var plan = GetView(_planId);

            var first = plan.CreateViewTemplate();
            var second = plan.CreateViewTemplate();
            if (first is null || second is null)
                return;

            first.Name = "SC_Plan_Tpl_WithFilters";
            second.Name = "SC_Plan_Tpl_FiltersOff";
            second.SetNonControlledTemplateParameterIds(
                new List<ElementId> { new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS) });

            withFilters = first.Id.GetValue();
            filtersOff = second.Id.GetValue();
        });

        if (withFilters == 0 || filtersOff == 0)
        {
            Skip.Test("CreateViewTemplate не создал шаблон в этом шаблоне проекта");
        }

        return (withFilters, filtersOff);
    }

    private AgentPlanExecutor TemplateFilterPlanExecutor(long templateId) =>
        new(
            new TestToolRegistry(new IAgentTool[]
            {
                new ApplyViewTemplateTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
                new CreateViewFilterTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            }),
            new ImmediateAwaitableEvent(),
            new FixedConfirmation());

    private static AgentPlan TemplateThenFilterPlan(long viewId, long templateId) => new()
    {
        Goal = "Template the view, then add a per-view filter",
        Steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "tpl",
                ToolName = "apply_view_template",
                ArgumentsJson = @"{""items"":[{""viewId"":" + viewId +
                                @",""templateId"":" + templateId + "}]}",
            },
            new AgentPlanStep
            {
                StepId = "flt",
                ToolName = "create_view_filter",
                ArgumentsJson =
                    @"{""viewIds"":[" + viewId + @"],""name"":""SC_Plan_Filter""," +
                    @"""categories"":[""OST_Walls""],""conditions"":[{""parameter"":" +
                    @"""ALL_MODEL_INSTANCE_COMMENTS"",""op"":""eq"",""value"":""SC_Plan_X""}]," +
                    @"""visibility"":""hide""}",
                DependsOn = new[] { "tpl" }
            }
        }
    };

    /// <summary>
    /// F1 × WA: the skill advice "apply the template first, then create the
    /// filters" must work when the template does NOT include "V/G Overrides
    /// Filters" — a template that only controls visibility does not stop a
    /// filter step (the detector used to reject the plan).
    /// </summary>
    [Test]
    public async Task Plan_TemplateThenViewFilter_TemplateSkipsFilters_AppliesBoth()
    {
        var (_, filtersOff) = SeedTemplates();
        var executor = TemplateFilterPlanExecutor(filtersOff);

        var result = await executor.ExecuteAsync(
            TemplateThenFilterPlan(_planId, filtersOff),
            new AgentPlanExecutionContext { DocumentId = AgentDocumentIdentity.GetDocumentId(Doc) },
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var view = GetView(_planId);
        await Assert.That(view.ViewTemplateId.GetValue()).IsEqualTo(filtersOff);
        var filters = view.GetFilters();
        await Assert.That(filters.Count).IsEqualTo(1);
        await Assert.That(view.GetFilterVisibility(filters.First())).IsFalse();
    }

    /// <summary>
    /// F1 × WA: a template that DOES include "V/G Overrides Filters" owns the
    /// filter list of its views — the plan is rejected up front with the two
    /// workable paths named, instead of failing inside the filter step with
    /// view_controlled_by_template.
    /// </summary>
    [Test]
    public async Task Plan_TemplateThenViewFilter_TemplateControlsFilters_RejectedWithEscape()
    {
        var (withFilters, _) = SeedTemplates();
        var executor = TemplateFilterPlanExecutor(withFilters);

        var result = await executor.ExecuteAsync(
            TemplateThenFilterPlan(_planId, withFilters),
            new AgentPlanExecutionContext { DocumentId = AgentDocumentIdentity.GetDocumentId(Doc) },
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("plan_step_conflict");
        await Assert.That(result.Message!.Contains("filters", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Message.Contains(
            "parameters=[\"filters\"], included=false", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Message.Contains("viewIds=[" + withFilters + "]", StringComparison.Ordinal)).IsTrue();

        // Rejected before any step ran: the view keeps no template.
        await Assert.That(GetView(_planId).ViewTemplateId)
            .IsEqualTo(ElementId.InvalidElementId);
    }

    private static AgentPlanStep RenameStep(string id, long viewId, string newName) =>
        new()
        {
            StepId = id,
            ToolName = "rename_views",
            ArgumentsJson = RenameArgs(viewId, newName),
            ExpectedChanges = 1,
            ReadBackTool = "get_view_settings",
            ArgumentsReadBackJson = "{\"viewId\":" + viewId + ",\"fields\":[\"name\"]}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "name", Expected = newName }
            }
        };

    private static string RenameArgs(long viewId, string newName) =>
        "{\"items\":[{\"viewId\":" + viewId + ",\"newName\":\"" + newName + "\"}],\"dryRun\":false}";

    private View GetView(long id) =>
        (View)Doc.GetElement(ElementIdCompat.Create(id));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FixedConfirmation : IAgentConfirmationService
    {
        public List<AgentConfirmationRequest> Requests { get; } = new();

        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(AgentConfirmationResult.Approved);
        }
    }

    private sealed class TestToolRegistry : IToolRegistry
    {
        private readonly List<IAgentTool> _tools;
        private readonly Dictionary<string, IAgentTool> _byName;

        public TestToolRegistry(IEnumerable<IAgentTool> tools)
        {
            _tools = tools.ToList();
            _byName = _tools.ToDictionary(
                tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<IAgentTool> GetAll() => _tools;

        public IReadOnlyList<AgentToolDefinition> GetDefinitions() =>
            _tools.Select(tool => tool.Definition).ToList();

        public bool TryGet(string name, out IAgentTool? tool)
        {
            if (_byName.TryGetValue(name, out var found))
            {
                tool = found;
                return true;
            }

            tool = null;
            return false;
        }
    }
}
