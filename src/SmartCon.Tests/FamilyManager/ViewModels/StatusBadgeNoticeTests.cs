using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// #210: clickable status badges — the <see cref="StatusNotice"/> lists built
/// by batch rows and tree nodes, the worst-severity badge math, and the
/// status details dialog view-model.
/// </summary>
public sealed class StatusBadgeNoticeTests
{
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock = new();

    private const string ParentPath = "system://OST_PipeCurves";

    private static FamilyBatchImportItem MakeParentItem() =>
        new(
            FilePath: ParentPath,
            FileName: "Трубы",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            FamilySource: "system");

    private static FamilyBatchImportItem MakeChildItem(
        string fileName,
        FamilyHealthReport? healthReport = null) =>
        new(
            FilePath: $"loadable://{fileName}",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            FamilySource: "loadable",
            HealthReport: healthReport,
            DependencyLinks:
            [
                new FamilyDependencyLink(ParentPath, FamilyDependencyKind.Routing, $"{fileName}:Стандарт"),
            ]);

    private static FamilyBatchImportItem MakeOutdatedChildItem(string fileName) =>
        new(
            FilePath: $"loadable://{fileName}",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Duplicate,
            ExistingCatalogItemId: "existing-1",
            ExistingVersionLabel: "v2",
            MatchedVersionLabel: "v1",
            FamilySource: "loadable",
            DependencyLinks:
            [
                new FamilyDependencyLink(ParentPath, FamilyDependencyKind.SharedNested, null),
            ]);

    private static FamilyHealthReport HealthWithError() =>
        FamilyHealthReport.FromIssues(
        [
            new FamilyHealthIssue("TypeA", FamilyHealthIssueSeverity.Error, "Formula error"),
        ]);

    // ---- Batch row notices ----

    [Fact]
    public void Row_Dependency_InfoNoticeNoProblemBadge()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод")],
            _dialogMock.Object,
            _factoryMock.Object);

        var child = vm.Items[1];
        Assert.False(child.HasProblemNotices);
        var notice = Assert.Single(child.Notices);
        Assert.Equal(StatusNoticeSeverity.Info, notice.Severity);
        Assert.False(notice.HasExplanation);
        var parentName = Assert.Single(notice.Items!);
        Assert.Equal("Трубы", parentName);
        Assert.NotEmpty(child.DependencyBadgeTooltip);
    }

    [Fact]
    public void Row_GateFailedChild_ParentHasErrorNotice()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод", HealthWithError())],
            _dialogMock.Object,
            _factoryMock.Object);

        var parent = vm.Items[0];
        Assert.True(parent.HasProblemNotices);
        Assert.Equal(StatusNoticeSeverity.Error, parent.ProblemBadgeSeverity);
        Assert.NotEmpty(parent.ProblemBadgeTooltip);
        var notice = Assert.Single(parent.Notices);
        Assert.Equal(StatusNoticeSeverity.Error, notice.Severity);
        Assert.True(notice.HasExplanation);
        var failedName = Assert.Single(notice.Items!);
        Assert.Equal("ADSK_Отвод", failedName);
    }

    [Fact]
    public void Row_OutdatedNestedChild_ChildWarningAndParentBlockError()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeOutdatedChildItem("Фланец")],
            _dialogMock.Object,
            _factoryMock.Object);

        var child = vm.Items[1];
        Assert.True(child.HasProblemNotices);
        Assert.Equal(StatusNoticeSeverity.Warning, child.ProblemBadgeSeverity);
        // Child = warning (outdated nested) + info (dependency-of).
        Assert.Contains(child.Notices, n =>
            n.Severity == StatusNoticeSeverity.Warning
            && n.Explanation!.Contains("v1")
            && n.Explanation.Contains("v2"));
        Assert.Contains(child.Notices, n => n.Severity == StatusNoticeSeverity.Info);

        var parent = vm.Items[0];
        Assert.Equal(StatusNoticeSeverity.Error, parent.ProblemBadgeSeverity);
        var notice = Assert.Single(parent.Notices);
        Assert.Equal(StatusNoticeSeverity.Error, notice.Severity);
        var blockedLine = Assert.Single(notice.Items!);
        Assert.Contains("Фланец", blockedLine);
    }

    [Fact]
    public void Row_CrossNameDuplicate_WarningNotice()
    {
        var item = MakeOutdatedChildItem("Фланец") with
        {
            IsCrossNameDuplicate = true,
            MatchedItemName = "Фланец ГОСТ",
        };
        var row = new FamilyBatchImportRow(item);

        Assert.True(row.HasProblemNotices);
        Assert.Contains(row.Notices, n =>
            n.Severity == StatusNoticeSeverity.Warning
            && n.Items != null
            && n.Items[0].Contains("Фланец ГОСТ"));
    }

    [Fact]
    public void Row_MarkerResolved_InfoNotice()
    {
        var item = MakeOutdatedChildItem("Фланец") with
        {
            MatchedVersionLabel = "v2",
            IsMarkerResolvedVersion = true,
        };
        var row = new FamilyBatchImportRow(item);

        Assert.False(row.HasProblemNotices);
        Assert.Contains(row.Notices, n =>
            n.Severity == StatusNoticeSeverity.Info && n.Explanation!.Contains("v2"));
    }

    [Fact]
    public void Row_PlainNew_NoNotices()
    {
        var row = new FamilyBatchImportRow(MakeParentItem());

        Assert.Empty(row.Notices);
        Assert.False(row.HasProblemNotices);
    }

    [Fact]
    public void Row_MarkerOnlyTopLevel_ShowsInfoBadge()
    {
        // A TOP-LEVEL row (no dependency links) whose version came from the
        // verification marker: neither the paperclip nor the problem
        // triangle applies — the muted info badge keeps the explanation
        // reachable.
        var item = MakeOutdatedChildItem("Фланец") with
        {
            MatchedVersionLabel = "v2",
            IsMarkerResolvedVersion = true,
            DependencyLinks = null,
        };
        var row = new FamilyBatchImportRow(item);

        Assert.False(row.IsDependency);
        Assert.False(row.HasProblemNotices);
        Assert.True(row.ShowInfoBadge);
        Assert.NotEmpty(row.InfoBadgeTooltip);
    }

    // ---- Batch row → details dialog wiring ----

    [Fact]
    public void Row_OpenInfoDetails_ShowsInfoNoticesOnlyNoActions()
    {
        object? shown = null;
        _dialogMock
            .Setup(d => d.ShowStatusDetails(It.IsAny<object>()))
            .Callback<object>(vm => shown = vm)
            .Returns((bool?)null);

        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод", HealthWithError())],
            _dialogMock.Object,
            _factoryMock.Object);

        // Paperclip: related-elements view — info notices only, and NO
        // actions even though the row has a health report (warnings and
        // actions live exclusively behind the problem triangle).
        var child = vm.Items[1];
        child.OpenInfoDetailsCommand.Execute(null);

        var details = Assert.IsType<StatusDetailsViewModel>(shown);
        Assert.Equal(child.FileName, details.Title);
        Assert.NotEmpty(details.Notices);
        Assert.All(details.Notices, n => Assert.Equal(StatusNoticeSeverity.Info, n.Severity));
        Assert.Empty(details.Actions);
    }

    [Fact]
    public void Row_OpenStatusDetails_ProblemViewShowsWarningAndReportAction()
    {
        object? shown = null;
        _dialogMock
            .Setup(d => d.ShowStatusDetails(It.IsAny<object>()))
            .Callback<object>(vm => shown = vm)
            .Returns((bool?)null);

        var outdatedWithReport = MakeOutdatedChildItem("Фланец") with
        {
            HealthReport = HealthWithError(),
        };
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), outdatedWithReport],
            _dialogMock.Object,
            _factoryMock.Object);

        var child = vm.Items[1];
        child.OpenStatusDetailsCommand.Execute(null);

        var details = Assert.IsType<StatusDetailsViewModel>(shown);
        Assert.NotEmpty(details.Notices);
        Assert.All(details.Notices, n => Assert.True(n.Severity >= StatusNoticeSeverity.Warning));
        // HealthReport exists → the validation report follow-up is offered.
        Assert.Single(details.Actions);
    }

    [Fact]
    public void Row_OpenStatusDetails_ReportActionOpensValidationReport()
    {
        StatusDetailsViewModel? shown = null;
        _dialogMock
            .Setup(d => d.ShowStatusDetails(It.IsAny<object>()))
            .Callback<object>(vm => shown = (StatusDetailsViewModel)vm)
            .Returns((bool?)null);

        var outdatedWithReport = MakeOutdatedChildItem("Фланец") with
        {
            HealthReport = HealthWithError(),
        };
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), outdatedWithReport],
            _dialogMock.Object,
            _factoryMock.Object);

        vm.Items[1].OpenStatusDetailsCommand.Execute(null);
        var action = Assert.Single(shown!.Actions);
        action.Execute();

        _factoryMock.Verify(f => f.CreateValidationReportViewModel(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<FamilyHealthReport?>(), It.IsAny<FamilyValidationReport?>(),
            It.IsAny<int>()), Times.Once);
        _dialogMock.Verify(d => d.ShowValidationReport(It.IsAny<object>()), Times.Once);
    }

    // ---- StatusDetailsViewModel ----

    [Fact]
    public void DetailsVm_WorstSeverity_IsMax()
    {
        var vm = new StatusDetailsViewModel(
            "Fam", null,
            [
                new StatusNotice(StatusNoticeSeverity.Info, "i", "x"),
                new StatusNotice(StatusNoticeSeverity.Error, "e", "x"),
                new StatusNotice(StatusNoticeSeverity.Warning, "w", "x"),
            ]);

        Assert.Equal(StatusNoticeSeverity.Error, vm.WorstSeverity);
        Assert.False(vm.HasSubtitle);
        Assert.False(vm.HasActions);
    }

    [Fact]
    public void DetailsVm_EmptyNotices_InfoSeverity()
    {
        var vm = new StatusDetailsViewModel("Fam", null, []);
        Assert.Equal(StatusNoticeSeverity.Info, vm.WorstSeverity);
    }

    [Fact]
    public void DetailsVm_RunAction_ClosesThenExecutes()
    {
        var actionRan = false;
        var closeFired = false;
        var vm = new StatusDetailsViewModel(
            "Fam", null, [],
            [new StatusDetailsAction("Do", () => actionRan = true)]);
        vm.RequestClose += _ => closeFired = true;

        vm.RunActionCommand.Execute(vm.Actions[0]);

        Assert.True(closeFired);
        Assert.True(actionRan);
    }

    [Fact]
    public void DetailsVm_Close_RequestsClose()
    {
        bool? result = null;
        var fired = false;
        var vm = new StatusDetailsViewModel("Fam", null, []);
        vm.RequestClose += r =>
        {
            fired = true;
            result = r;
        };

        vm.CloseCommand.Execute(null);

        Assert.True(fired);
        Assert.True(result);
    }

    [Fact]
    public void DetailsVm_SingleAction_DirectButtonShape()
    {
        var vm = new StatusDetailsViewModel(
            "Fam", null, [],
            [new StatusDetailsAction("Обновить", () => { })]);

        Assert.True(vm.HasSingleAction);
        Assert.False(vm.HasActionMenu);
        Assert.Equal("Обновить", vm.SingleAction!.Label);
        Assert.Null(vm.ActionsMenuLabel);
    }

    [Fact]
    public void DetailsVm_TwoActions_SplitButtonShape()
    {
        var vm = new StatusDetailsViewModel(
            "Fam", null, [],
            [
                new StatusDetailsAction("Сохранить параметры", () => { }),
                new StatusDetailsAction("Перезаписать параметры", () => { }),
            ],
            actionsMenuLabel: "Обновить все типы");

        Assert.False(vm.HasSingleAction);
        Assert.True(vm.HasActionMenu);
        Assert.Equal("Обновить все типы", vm.ActionsMenuLabel);
    }

    [Fact]
    public void DetailsVm_MenuAction_ClosesThenExecutes()
    {
        var actionRan = false;
        var closeFired = false;
        var vm = new StatusDetailsViewModel(
            "Fam", null, [],
            [
                new StatusDetailsAction("A", () => actionRan = true),
                new StatusDetailsAction("B", () => { }),
            ],
            actionsMenuLabel: "Обновить все типы");
        vm.RequestClose += _ => closeFired = true;

        vm.RunActionCommand.Execute(vm.Actions[0]);

        Assert.True(closeFired);
        Assert.True(actionRan);
    }

    // ---- Tree node notices ----

    private static readonly IFamilyAssetService AssetService = new Mock<IFamilyAssetService>().Object;

    private static FamilyLeafNodeViewModel CreateLeaf() =>
        new(
            new FamilyCatalogItemRow { Id = "item1", Name = "FamA" },
            AssetService,
            currentRevitVersion: 2025);

    [Fact]
    public void Leaf_Stale_WarningNoticeAndBadge()
    {
        var leaf = CreateLeaf();
        Assert.False(leaf.HasProblemBadge);
        Assert.Empty(leaf.StatusNotices);

        leaf.IsStale = true;
        leaf.StaleReason = StaleReason.VersionMismatch;

        Assert.True(leaf.HasProblemBadge);
        Assert.NotEmpty(leaf.ProblemBadgeTooltip);
        var notice = Assert.Single(leaf.StatusNotices);
        Assert.Equal(StatusNoticeSeverity.Warning, notice.Severity);
        Assert.NotEmpty(notice.Explanation!);
    }

    [Fact]
    public void Leaf_OutdatedDependencies_WarningNotice()
    {
        var leaf = CreateLeaf();

        leaf.HasOutdatedDependencies = true;
        leaf.OutdatedDependencyLines = ["Фланец (зашита v1 → активна v2)"];

        Assert.True(leaf.HasProblemBadge);
        Assert.Contains(leaf.StatusNotices, n =>
            n.Severity == StatusNoticeSeverity.Warning
            && n.Items != null
            && n.Items[0].Contains("Фланец"));
    }

    [Fact]
    public void Leaf_DependencyReferenced_InfoNotice()
    {
        var leaf = CreateLeaf();

        leaf.IsDependencyReferenced = true;
        leaf.DependencyReferencedLines = ["Фланцевая пара (v2)"];

        Assert.False(leaf.HasProblemBadge);
        Assert.NotEmpty(leaf.DependencyReferencedBadgeTooltip);
        var notice = Assert.Single(leaf.StatusNotices);
        Assert.Equal(StatusNoticeSeverity.Info, notice.Severity);
        var line = Assert.Single(notice.Items!);
        Assert.Contains("Фланцевая пара", line);
    }

    [Fact]
    public void Category_HasStale_WarningNoticeWithCount()
    {
        var category = new CategoryNodeViewModel("cat1", "Фланцы", null, "Фланцы");
        Assert.Empty(category.StatusNotices);

        category.HasStale = true;
        category.StaleCount = 2;

        var notice = Assert.Single(category.StatusNotices);
        Assert.Equal(StatusNoticeSeverity.Warning, notice.Severity);
        Assert.Contains("2", notice.Explanation);
        Assert.NotEmpty(category.StaleBadgeTooltip);
    }

    [Fact]
    public void Category_NotStale_NoNotices()
    {
        var category = new CategoryNodeViewModel("cat1", "Фланцы", null, "Фланцы");

        category.HasStale = false;
        category.StaleCount = 0;

        Assert.Empty(category.StatusNotices);
    }

    [Fact]
    public void TypeNode_DotTooltip_FollowsPresenceState()
    {
        var node = new FamilyTypeNodeViewModel("item1", "TypeA");

        // Grey: not in project → «Загрузить и разместить тип».
        Assert.Equal(TypePresenceState.NotInProject, node.PresenceState);
        Assert.NotEmpty(node.PresenceBadgeTooltip);

        // Orange: stale in project → «Обновить и разместить» (distinct
        // from the plain blue «Разместить тип»).
        node.IsInProject = true;
        node.IsStaleInProject = true;
        Assert.Equal(TypePresenceState.StaleInProject, node.PresenceState);
        var staleTooltip = node.PresenceBadgeTooltip;

        node.IsStaleInProject = false;
        Assert.Equal(TypePresenceState.InProject, node.PresenceState);
        Assert.NotEqual(staleTooltip, node.PresenceBadgeTooltip);
    }
}
