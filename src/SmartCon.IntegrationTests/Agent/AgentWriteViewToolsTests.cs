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
        await Assert.That(result.ErrorCode).IsEqualTo("all_items_failed");
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
