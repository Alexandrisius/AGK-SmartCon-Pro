using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed class AgentWriteViewToolsTests : RevitApiTest
{
    private const string LevelName = "SC_AgentWrite_Level";
    private const string PlanName = "SC_AgentWrite_Plan";
    private const string TargetName = "SC_AgentWrite_Target";
    private const string MissingViewId = "987654321";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _planId;
    private long _targetId;
    private long _templateId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed agent write views", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName);
            _planId = plan.Id.GetValue();

            var target = ModelSeed.CreateViewPlanChecked(doc, level.Id, TargetName);
            _targetId = target.Id.GetValue();

            _templateId = plan.CreateViewTemplate()?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task RenameViews_DryRun_DoesNotChangeModel()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_Renamed\"}],\"dryRun\":true}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RenameViews_Apply_RenamesAndRecordsChangeSet()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_Renamed\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetView(_planId).Name).IsEqualTo("SC_AgentWrite_Renamed");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(1);

        var set = _changeSets.Last!;
        await Assert.That(set.Header.Operation).IsEqualTo("rename_views");
        await Assert.That(set.Header.AppliedAt).IsNotNull();
        await Assert.That(set.Items.Count).IsEqualTo(1);
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("view_name");
        await Assert.That(set.Items[0].OldValue).IsEqualTo(PlanName);
    }

    [Test]
    public async Task RenameViews_DuplicateName_FailsWithoutChanging()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"" + TargetName + "\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        // The dominant per-item code is promoted to the top (external run 6): a
        // duplicate_name hidden inside failed[] was the reason for this change.
        await Assert.That(result.ErrorCode).IsEqualTo("duplicate_name");
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RenameViews_WithoutApproval_ReturnsConfirmationRequired()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_NoApproval\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: false, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirmation_required");
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
    }

    [Test]
    public async Task RenameViews_AtomicWithInvalidId_RollsBackEverything()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_Atomic\"},{\"viewId\":" + MissingViewId + ",\"newName\":\"SC_Nope\"}],\"atomic\":true,\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("atomic_aborted");
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RenameViews_Swap_AppliesBothAndUndoRestores()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"" + TargetName + "\"}," +
            "{\"viewId\":" + _targetId + ",\"newName\":\"" + PlanName + "\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetView(_planId).Name).IsEqualTo(TargetName);
        await Assert.That(GetView(_targetId).Name).IsEqualTo(PlanName);

        var set = _changeSets!.Last!;
        await Assert.That(set.Items.Count).IsEqualTo(2);
        await Assert.That(set.Items.Any(item => item.OldValue == PlanName)).IsTrue();
        await Assert.That(set.Items.Any(item => item.OldValue == TargetName)).IsTrue();

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
        await Assert.That(GetView(_targetId).Name).IsEqualTo(TargetName);
    }

    [Test]
    public async Task RenameLevels_Swap_SwapsBothAndUndoRestores()
    {
        long secondLevelId = 0;
        _transactions!.RunInTransaction(Doc, "Seed second level", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 6.0);
            level.Name = "SC_AgentWrite_LevelB";
            secondLevelId = level.Id.GetValue();
        });

        var tool = new RenameLevelsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"levelId\":" + _levelId + ",\"newName\":\"SC_AgentWrite_LevelB\"}," +
            "{\"levelId\":" + secondLevelId + ",\"newName\":\"" + LevelName + "\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetLevel(_levelId).Name).IsEqualTo("SC_AgentWrite_LevelB");
        await Assert.That(GetLevel(secondLevelId).Name).IsEqualTo(LevelName);

        var set = _changeSets!.Last!;
        await Assert.That(set.Items.Count).IsEqualTo(2);
        await Assert.That(set.Items.Any(item => item.OldValue == LevelName)).IsTrue();

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(GetLevel(_levelId).Name).IsEqualTo(LevelName);
        await Assert.That(GetLevel(secondLevelId).Name).IsEqualTo("SC_AgentWrite_LevelB");
    }

    [Test]
    public async Task ConfigureViews_RenameTemplate_IsRejected()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new ConfigureViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _templateId + ",\"newName\":\"SC_AgentWrite_TemplateRenamed\"}]," +
            "\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed[0].GetProperty("errorCode").GetString()).IsEqualTo("view_is_template");
    }

    [Test]
    public async Task WriteTools_InputSchemas_AreValidJsonObjects()
    {
        var context = new StubRevitContext(Doc);
        var tools = new List<SmartCon.Core.Services.Interfaces.Agent.IAgentTool>
        {
            new RenameViewsTool(context, _transactions!, _changeSets!),
            new RenameLevelsTool(context, _transactions!, _changeSets!),
            new ApplyViewTemplateTool(context, _transactions!, _changeSets!),
            new SetViewRangeTool(context, _transactions!, _changeSets!),
            new SetViewCropTool(context, _transactions!, _changeSets!),
            new SetViewVisibilityTool(context, _transactions!, _changeSets!),
            new ConfigureViewsTool(context, _transactions!, _changeSets!),
            new CopyViewSettingsTool(context, _transactions!, _changeSets!),
            new SetParametersTool(context, _transactions!, _changeSets!),
            new RemapParameterValuesTool(context, _transactions!, _changeSets!),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            using var document = JsonDocument.Parse(tool.Definition.InputSchemaJson);
            var root = document.RootElement;
            await Assert.That(root.ValueKind).IsEqualTo(JsonValueKind.Object);
            await Assert.That(root.TryGetProperty("type", out var type)).IsTrue();
            await Assert.That(type.GetString()).IsEqualTo("object");
            await Assert.That(names.Add(tool.Definition.Name)).IsTrue();
        }
    }

    [Test]
    public async Task RenameLevels_SwapWithCascadeViews_SwapsNamesAndUndoRestores()
    {
        long secondLevelId = 0;
        _transactions!.RunInTransaction(Doc, "Seed cascade pair", doc =>
        {
            var first = (Level)doc.GetElement(ElementIdCompat.Create(_levelId));
            ModelSeed.CreateViewPlanChecked(doc, first.Id, LevelName);

            var second = ModelSeed.CreateLevel(doc, 6.0);
            second.Name = "SC_AgentWrite_LevelB";
            secondLevelId = second.Id.GetValue();
            ModelSeed.CreateViewPlanChecked(doc, second.Id, "SC_AgentWrite_LevelB");
        });

        var tool = new RenameLevelsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"levelId\":" + _levelId + ",\"newName\":\"SC_AgentWrite_LevelB\"}," +
            "{\"levelId\":" + secondLevelId + ",\"newName\":\"" + LevelName + "\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetLevel(_levelId).Name).IsEqualTo("SC_AgentWrite_LevelB");
        await Assert.That(GetLevel(secondLevelId).Name).IsEqualTo(LevelName);
        await Assert.That(PlanForLevel(_levelId).Name).IsEqualTo("SC_AgentWrite_LevelB");
        await Assert.That(PlanForLevel(secondLevelId).Name).IsEqualTo(LevelName);

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(PlanForLevel(_levelId).Name).IsEqualTo(LevelName);
        await Assert.That(PlanForLevel(secondLevelId).Name).IsEqualTo("SC_AgentWrite_LevelB");
    }

    [Test]
    public async Task ConfigureViews_PartialFailure_StillRecordsChangeSet()
    {
        var tool = new ConfigureViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _targetId + ",\"newName\":\"SC_AgentWrite_Partial\"," +
            "\"templateId\":" + _planId + "}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(GetView(_targetId).Name).IsEqualTo("SC_AgentWrite_Partial");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(1);
        await Assert.That(_changeSets.Last!.Items[0].ParameterName).IsEqualTo("view_name");

        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("invalid_template");
    }

    [Test]
    public async Task UndoLast_RenameTargetTaken_ReportsConflictAndKeepsChangeSet()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_Taken\"}],\"dryRun\":false}");
        var applied = tool.ExecuteForTests(args, approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();

        _transactions!.RunInTransaction(Doc, "Take old name", doc =>
        {
            var target = (View)doc.GetElement(ElementIdCompat.Create(_targetId));
            target.Name = PlanName;
        });

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsFalse();
        await Assert.That(undo.ErrorCode).IsEqualTo("undo_conflict");
        await Assert.That(GetView(_planId).Name).IsEqualTo("SC_AgentWrite_Taken");
        await Assert.That(_changeSets!.Last!.Header.AppliedAt).IsNotNull();
    }

    [Test]
    public async Task SetViewCrop_KeepsShapeOnVisibilityToggle_ExtentsRemoveIt_UndoRestores()
    {
        _transactions!.RunInTransaction(Doc, "Seed crop shape", doc =>
        {
            var view = (View)doc.GetElement(ElementIdCompat.Create(_planId));
            view.CropBoxActive = true;
            view.CropBoxVisible = true;
            var loop = CurveLoop.Create(new List<Curve>
            {
                Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0)),
                Line.CreateBound(new XYZ(10, 0, 0), new XYZ(0, 10, 0)),
                Line.CreateBound(new XYZ(0, 10, 0), new XYZ(0, 0, 0))
            });
            view.GetCropRegionShapeManager().SetCropShape(loop);
        });

        var plan = GetView(_planId);
        var manager = plan.GetCropRegionShapeManager();
        if (!manager.ShapeSet)
            Skip.Test("Шаблон проекта не позволил задать нефигурный crop (CanHaveShape=false)");

        var cropTool = new SetViewCropTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var toggle = cropTool.ExecuteForTests(
            Parse("{\"items\":[{\"viewId\":" + _planId + ",\"crop\":{\"visible\":false}}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(toggle.Success).IsTrue();
        await Assert.That(plan.GetCropRegionShapeManager().ShapeSet).IsTrue();

        var rectangle = cropTool.ExecuteForTests(
            Parse("{\"items\":[{\"viewId\":" + _planId +
                  ",\"crop\":{\"minMm\":[-5000,-5000],\"maxMm\":[5000,5000]}}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(rectangle.Success).IsTrue();
        await Assert.That(plan.GetCropRegionShapeManager().ShapeSet).IsFalse();

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(plan.GetCropRegionShapeManager().ShapeSet).IsTrue();
    }

    [Test]
    public async Task RenameViews_DuplicateItem_IsRejected()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_DupA\"}," +
            "{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_DupB\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(GetView(_planId).Name).IsEqualTo("SC_AgentWrite_DupA");
        await Assert.That(_changeSets!.Last!.Items.Count).IsEqualTo(1);

        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("duplicate_item");
    }

    [Test]
    public async Task UndoLast_AlreadyRestored_ClosesChangeSet()
    {
        var tool = CreateRenameViewsTool();
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"newName\":\"SC_AgentWrite_Closed\"}],\"dryRun\":false}");
        var applied = tool.ExecuteForTests(args, approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();

        _transactions!.RunInTransaction(Doc, "Restore name manually", doc =>
        {
            var view = (View)doc.GetElement(ElementIdCompat.Create(_planId));
            view.Name = PlanName;
        });

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsFalse();
        await Assert.That(undo.ErrorCode).IsEqualTo("nothing_to_undo");
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
        await Assert.That(_changeSets!.Last!.Header.AppliedAt).IsNull();
    }

    private ViewPlan PlanForLevel(long levelId) => new FilteredElementCollector(Doc)
        .OfClass(typeof(ViewPlan))
        .Cast<ViewPlan>()
        .First(view => view.GenLevel is not null
                       && view.GenLevel.Id.GetValue() == levelId
                       && (view.Name == LevelName || view.Name == "SC_AgentWrite_LevelB"));

    private Level GetLevel(long id) => (Level)Doc.GetElement(ElementIdCompat.Create(id));

    [Test]
    public async Task ApplyViewTemplate_AppliesAndReadsBack()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new ApplyViewTemplateTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _targetId + ",\"templateId\":" + _templateId + "}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetView(_targetId).ViewTemplateId.GetValue()).IsEqualTo(_templateId);
        await Assert.That(_changeSets!.Last!.Items[0].ParameterName).IsEqualTo("view_template");
    }

    [Test]
    public async Task SetViewTemplateInclusion_DetachesViewRangeAndAllowsWrite()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        _transactions!.RunInTransaction(Doc, "Assign template", doc =>
            GetView(_targetId).ViewTemplateId = ElementIdCompat.Create(_templateId));

        var tool = new SetViewTemplateInclusionTool(
            new StubRevitContext(Doc), _transactions, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"templateId\":" + _templateId +
                  ",\"parameters\":[\"viewRange\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                result.ErrorCode + ": " + result.Message + " data=" + result.Data);
        }

        await Assert.That(result.Data!.Value.GetProperty("affectedViewCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);

        var template = (View)Doc.GetElement(ElementIdCompat.Create(_templateId));
        var rangeId = new ElementId(BuiltInParameter.PLAN_VIEW_RANGE).GetValue();
        await Assert.That(template.GetNonControlledTemplateParameterIds()
                .Any(id => id.GetValue() == rangeId))
            .IsTrue();
        await Assert.That(_changeSets!.Last!.Items[0].ParameterName)
            .IsEqualTo("view_template_inclusion");

        var rangeTool = new SetViewRangeTool(
            new StubRevitContext(Doc), _transactions, _changeSets!);
        var rangeResult = rangeTool.ExecuteForTests(
            Parse("{\"items\":[{\"viewId\":" + _targetId +
                  ",\"viewRange\":{\"top\":{\"levelId\":" + _levelId + ",\"offsetMm\":2500}," +
                  "\"cutPlane\":{\"levelId\":" + _levelId + ",\"offsetMm\":1000}," +
                  "\"bottom\":{\"levelId\":" + _levelId + ",\"offsetMm\":0}," +
                  "\"viewDepth\":{\"levelId\":" + _levelId + ",\"offsetMm\":-1200}}}]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(rangeResult.Success).IsTrue();
        var plan = (ViewPlan)GetView(_targetId);
        var range = plan.GetViewRange();
        var topMm = UnitUtils.ConvertFromInternalUnits(
            range.GetOffset(PlanViewPlane.TopClipPlane),
            UnitTypeId.Millimeters);
        await Assert.That(Math.Round(topMm)).IsEqualTo(2500);
    }

    [Test]
    public async Task SetViewTemplateInclusion_CropCannotBeDetached()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        _transactions!.RunInTransaction(Doc, "Assign template", doc =>
            GetView(_targetId).ViewTemplateId = ElementIdCompat.Create(_templateId));

        var tool = new SetViewTemplateInclusionTool(
            new StubRevitContext(Doc), _transactions, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"templateId\":" + _templateId +
                  ",\"parameters\":[\"crop\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("parameter_not_in_template");
        await Assert.That(result.Message).Contains("Crop Region");
    }

    [Test]
    public async Task SetViewTemplateInclusion_DryRun_DoesNotChangeTemplate()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new SetViewTemplateInclusionTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"templateId\":" + _templateId +
                  ",\"parameters\":[\"viewRange\"],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var template = (View)Doc.GetElement(ElementIdCompat.Create(_templateId));
        var rangeId = new ElementId(BuiltInParameter.PLAN_VIEW_RANGE).GetValue();
        await Assert.That(template.GetNonControlledTemplateParameterIds()
                .Any(id => id.GetValue() == rangeId))
            .IsFalse();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetViewTemplateInclusion_Undo_RestoresControl()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new SetViewTemplateInclusionTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"templateId\":" + _templateId +
                  ",\"parameters\":[\"viewRange\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(result.Success).IsTrue();

        var undo = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undoResult = undo.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undoResult.Success).IsTrue();
        var template = (View)Doc.GetElement(ElementIdCompat.Create(_templateId));
        var rangeId = new ElementId(BuiltInParameter.PLAN_VIEW_RANGE).GetValue();
        await Assert.That(template.GetNonControlledTemplateParameterIds()
                .Any(id => id.GetValue() == rangeId))
            .IsFalse();
    }

    [Test]
    public async Task ApplyViewTemplate_Select_ResolvesViewSet()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new ApplyViewTemplateTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"select\":{\"type\":\"FloorPlan\",\"nameContains\":\"SC_AgentWrite\"}," +
                  "\"templateId\":" + _templateId + ",\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var selected = result.Data!.Value.GetProperty("selectedViewIds");
        await Assert.That(selected.GetArrayLength()).IsGreaterThanOrEqualTo(2);
        await Assert.That(result.Data.Value.GetProperty("selectorMatched").GetInt32())
            .IsEqualTo(selected.GetArrayLength());
        await Assert.That(GetView(_targetId).ViewTemplateId)
            .IsEqualTo(ElementId.InvalidElementId);
    }

    [Test]
    public async Task ConfigureViews_Select_ResolvesViewSet()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new ConfigureViewsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"select\":{\"type\":\"FloorPlan\",\"nameContains\":\"SC_AgentWrite\"}," +
                  "\"settings\":{\"templateId\":" + _templateId + "},\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var selected = result.Data!.Value.GetProperty("selectedViewIds");
        await Assert.That(selected.GetArrayLength()).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task SetViewTemplateInclusion_ByViewId_UsesAssignedTemplate()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        _transactions!.RunInTransaction(Doc, "Assign template", doc =>
            GetView(_targetId).ViewTemplateId = ElementIdCompat.Create(_templateId));

        var tool = new SetViewTemplateInclusionTool(
            new StubRevitContext(Doc), _transactions, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"viewId\":" + _targetId +
                  ",\"parameters\":[\"visibility\"],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("templateId").GetInt64())
            .IsEqualTo(_templateId);
    }

    [Test]
    public async Task SetViewTemplateInclusion_Include_RestoresControl()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new SetViewTemplateInclusionTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var detach = tool.ExecuteForTests(
            Parse("{\"templateId\":" + _templateId +
                  ",\"parameters\":[\"viewRange\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(detach.Success).IsTrue();

        var restore = tool.ExecuteForTests(
            Parse("{\"templateId\":" + _templateId +
                  ",\"parameters\":[\"viewRange\"],\"included\":true,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(restore.Success).IsTrue();

        var template = (View)Doc.GetElement(ElementIdCompat.Create(_templateId));
        var rangeId = new ElementId(BuiltInParameter.PLAN_VIEW_RANGE).GetValue();
        await Assert.That(template.GetNonControlledTemplateParameterIds()
                .Any(id => id.GetValue() == rangeId))
            .IsFalse();
    }

    [Test]
    public async Task ApplyViewTemplate_NonTemplateArgument_ReturnsInvalidTemplate()
    {
        var tool = new ApplyViewTemplateTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _targetId + ",\"templateId\":" + _planId + "}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("invalid_template");
    }

    [Test]
    public async Task ApplyViewTemplate_IncompatibleFamily_ReturnsTemplateNotApplicable()
    {
        // Tester report C3: a schedule template on a floor plan must be rejected by
        // the family-compatibility check even though the target view has no template.
        long scheduleTemplateId = 0;
        _transactions!.RunInTransaction(Doc, "Seed schedule template", doc =>
        {
            var schedule = ModelSeed.CreateScheduleChecked(doc);
            scheduleTemplateId = schedule.CreateViewTemplate()?.Id.GetValue() ?? 0;
        });

        if (scheduleTemplateId == 0)
            Skip.Test("Шаблон ведомости не создан");

        var tool = new ApplyViewTemplateTool(new StubRevitContext(Doc), _transactions, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _targetId + ",\"templateId\":" + scheduleTemplateId +
            "}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("template_view_type_mismatch");
        await Assert.That(GetView(_targetId).ViewTemplateId)
            .IsEqualTo(ElementId.InvalidElementId);
    }

    [Test]
    public async Task SetViewRange_AppliesAndReadsBack()
    {
        var tool = new SetViewRangeTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"viewRange\":{\"top\":{\"levelId\":" + _levelId + ",\"offsetMm\":3000},\"cutPlane\":{\"levelId\":" + _levelId + ",\"offsetMm\":1200},\"bottom\":{\"levelId\":" + _levelId + ",\"offsetMm\":0},\"viewDepth\":{\"levelId\":" + _levelId + ",\"offsetMm\":-1000}}}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var plan = (ViewPlan)GetView(_planId);
        var range = plan.GetViewRange();
        var topMm = UnitUtils.ConvertFromInternalUnits(
            range.GetOffset(PlanViewPlane.TopClipPlane),
            UnitTypeId.Millimeters);
        await Assert.That(Math.Round(topMm)).IsEqualTo(3000);
        await Assert.That(_changeSets!.Last!.Items[0].ParameterName).IsEqualTo("view_range");
    }

    [Test]
    public async Task SetViewRange_OnThreeDView_ReturnsUnsupportedType()
    {
        var threeD = new FilteredElementCollector(Doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(view => !view.IsTemplate);
        if (threeD is null)
            Skip.Test("В документе нет 3D-вида");

        var tool = new SetViewRangeTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + threeD.Id.GetValue() + ",\"viewRange\":{\"top\":{\"levelId\":" + _levelId + ",\"offsetMm\":3000}}}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("unsupported_view_type");
    }

    [Test]
    public async Task SetViewCrop_CopiesFromSourceView()
    {
        _transactions!.RunInTransaction(Doc, "Seed crop", doc =>
        {
            var source = (View)doc.GetElement(ElementIdCompat.Create(_planId));
            source.CropBoxActive = true;
            source.CropBox = new BoundingBoxXYZ
            {
                Min = new XYZ(-10, -5, 0),
                Max = new XYZ(10, 5, 0)
            };
        });

        var tool = new SetViewCropTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _targetId + ",\"crop\":{\"copyFromViewId\":" + _planId + "}}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var sourceBox = GetView(_planId).CropBox;
        var targetBox = GetView(_targetId).CropBox;
        await Assert.That(targetBox).IsNotNull();
        await Assert.That(Math.Abs(targetBox!.Min.X - sourceBox!.Min.X) < 1e-6).IsTrue();
        await Assert.That(Math.Abs(targetBox.Max.Y - sourceBox.Max.Y) < 1e-6).IsTrue();
        await Assert.That(GetView(_targetId).CropBoxActive).IsTrue();
    }

    [Test]
    public async Task SetViewVisibility_HidesCategory()
    {
        var category = Category.GetCategory(Doc, BuiltInCategory.OST_Grids);
        if (category is null)
            Skip.Test("Категория OST_Grids недоступна");

        var tool = new SetViewVisibilityTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _planId + ",\"visibility\":{\"categories\":[{\"category\":\"OST_Grids\",\"hidden\":true}]}}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetView(_planId).GetCategoryHidden(category!.Id)).IsTrue();
        await Assert.That(_changeSets!.Last!.Items[0].ParameterName).IsEqualTo("visibility");
    }

    [Test]
    public async Task ConfigureViews_AppliesNameAndCrop()
    {
        _transactions!.RunInTransaction(Doc, "Seed crop", doc =>
        {
            var source = (View)doc.GetElement(ElementIdCompat.Create(_planId));
            source.CropBoxActive = true;
            source.CropBox = new BoundingBoxXYZ
            {
                Min = new XYZ(-8, -4, 0),
                Max = new XYZ(8, 4, 0)
            };
        });

        var tool = new ConfigureViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"viewId\":" + _targetId + ",\"newName\":\"SC_AgentWrite_Configured\",\"crop\":{\"copyFromViewId\":" + _planId + "}}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetView(_targetId).Name).IsEqualTo("SC_AgentWrite_Configured");
        await Assert.That(GetView(_targetId).CropBoxActive).IsTrue();
        await Assert.That(_changeSets!.Last!.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CopyViewSettings_CopiesCropToTargets()
    {
        _transactions!.RunInTransaction(Doc, "Seed crop", doc =>
        {
            var source = (View)doc.GetElement(ElementIdCompat.Create(_planId));
            source.CropBoxActive = true;
            source.CropBox = new BoundingBoxXYZ
            {
                Min = new XYZ(-6, -3, 0),
                Max = new XYZ(6, 3, 0)
            };
        });

        var tool = new CopyViewSettingsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"sourceViewId\":" + _planId + ",\"targetViewIds\":[" + _targetId + "],\"sections\":[\"crop\"],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetView(_targetId).CropBoxActive).IsTrue();
        var sourceBox = GetView(_planId).CropBox;
        var targetBox = GetView(_targetId).CropBox;
        await Assert.That(Math.Abs(targetBox!.Max.X - sourceBox!.Max.X) < 1e-6).IsTrue();
    }

    [Test]
    public async Task RenameLevels_RenamesCascadedView()
    {
        long cascadeLevelId = 0;
        _transactions!.RunInTransaction(Doc, "Seed cascade", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 6.0);
            level.Name = "SC_AgentWrite_Cascade";
            ModelSeed.CreateViewPlanChecked(doc, level.Id, "SC_AgentWrite_Cascade");
            cascadeLevelId = level.Id.GetValue();
        });

        var tool = new RenameLevelsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"items\":[{\"levelId\":" + cascadeLevelId + ",\"newName\":\"SC_AgentWrite_Cascade_New\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var level = (Level)Doc.GetElement(ElementIdCompat.Create(cascadeLevelId));
        await Assert.That(level.Name).IsEqualTo("SC_AgentWrite_Cascade_New");

        var cascadeView = new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => view.Name == "SC_AgentWrite_Cascade_New");
        await Assert.That(cascadeView).IsNotNull();
        await Assert.That(_changeSets!.Last!.Items[0].ParameterName).IsEqualTo("level_name");
    }

    private RenameViewsTool CreateRenameViewsTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private View GetView(long id) =>
        (View)Doc.GetElement(ElementIdCompat.Create(id));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
