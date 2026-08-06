using System.Collections.ObjectModel;
using System.Reflection;
using HelixToolkit.SharpDX;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

[Collection("Localization")]
public sealed class FamilyPropertiesViewModelTests
{
    private static (FamilyPropertiesViewModel vm, Mock<IFamilyAssetService> assetService, Mock<IFamilyGeometryPipeline> geometryPipeline) MakeVm(
        FamilyFactsData? factsData = null)
    {
        var assetService = new Mock<IFamilyAssetService>();
        var geometryPipeline = new Mock<IFamilyGeometryPipeline>();
        var fileResolver = new Mock<IFamilyFileResolver>();
        var factRepository = new Mock<IFamilyFactRepository>();

        assetService
            .Setup(x => x.GetAssetsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FamilyAsset>());

        factRepository
            .Setup(x => x.GetForItemAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(factsData ?? new FamilyFactsData(null, []));

        var vm = new FamilyPropertiesViewModel(
            catalogItemId: "catalog-1",
            name: "Test Family",
            description: null,
            categoryId: null,
            categoryPath: null,
            tags: [],
            contentStatus: ContentStatus.Active,
            versionLabel: "v1",
            createdAtText: null,
            updatedAtText: null,
            revitCategory: null,
            writableProvider: new Mock<IWritableFamilyCatalogProvider>().Object,
            catalogProvider: new Mock<IFamilyCatalogProvider>().Object,
            categoryRepository: new Mock<ICategoryRepository>().Object,
            assetService: assetService.Object,
            presetService: new Mock<IAttributePresetService>().Object,
            dialogService: new Mock<IFamilyManagerDialogService>().Object,
            bindingService: new Mock<ICategoryAttributeBindingService>().Object,
            valueRepository: new Mock<IAttributeValueRepository>().Object,
            runRepository: new Mock<IFamilyDataImportRunRepository>().Object,
            typeRepository: new Mock<IFamilyTypeRepository>().Object,
            attributeDefRepository: new Mock<IAttributeDefinitionRepository>().Object,
            viewModelFactory: new Mock<IFamilyManagerViewModelFactory>().Object,
            renameService: new Mock<IFamilyStorageRenameService>().Object,
            geometryPipeline: geometryPipeline.Object,
            fileResolver: fileResolver.Object,
            avatarCropService: new Mock<IAvatarCropService>().Object,
            updateState: new TestDoubles.FakeDatabaseUpdateStateService(),
            factRepository: factRepository.Object,
            categoryChangeGate: new Mock<ICategoryChangeGateService>().Object);

        return (vm, assetService, geometryPipeline);
    }

    private static FamilyAsset MakeModel3DAsset(string id, string typeName)
    {
        return new FamilyAsset(
            id,
            "catalog-1",
            "v1",
            FamilyAssetType.Model3D,
            $"preview-{id}.glb",
            $"path/{id}.glb",
            0,
            $"auto-extracted-preview:Test Family::{typeName}",
            DateTimeOffset.UtcNow,
            false);
    }

    [Fact]
    public async Task LoadAssetsAsync_DoesNotTrigger3DPreviewLoad()
    {
        var (vm, assetService, geometryPipeline) = MakeVm();

        await vm.LoadAssetsAsync(CancellationToken.None);

        geometryPipeline.Verify(
            x => x.RunAsync(
                It.IsAny<IReadOnlyList<FamilyGeometryPerType>?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        assetService.Verify(
            x => x.ResolveAssetPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadAssetsAsync_PopulatesModel3DAssetsAndSelectsFirstType()
    {
        var assets = new List<FamilyAsset>
        {
            MakeModel3DAsset("id-1", "Type A"),
            MakeModel3DAsset("id-2", "Type B")
        };

        var (vm, assetService, _) = MakeVm();
        assetService
            .Setup(x => x.GetAssetsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(assets);

        await vm.LoadAssetsAsync(CancellationToken.None);

        Assert.Equal(2, vm.Model3DAssets.Count);
        Assert.Equal("Type A", vm.Selected3DTypeName);
        Assert.Contains("Type A", vm.Available3DTypeNames);
        Assert.Contains("Type B", vm.Available3DTypeNames);
    }

    [Fact]
    public void Populate3DTypeNames_PreservesExistingSelection()
    {
        var (vm, _, _) = MakeVm();
        vm.Model3DAssets = new ObservableCollection<FamilyAsset>(
            new[]
            {
                MakeModel3DAsset("id-1", "Type A"),
                MakeModel3DAsset("id-2", "Type B")
            });

        vm.Populate3DTypeNames();
        Assert.Equal("Type A", vm.Selected3DTypeName);

        // Simulate refresh where Type A is gone but Type B remains.
        vm.Model3DAssets = new ObservableCollection<FamilyAsset>(
            new[]
            {
                MakeModel3DAsset("id-3", "Type B"),
                MakeModel3DAsset("id-4", "Type C")
            });

        vm.Populate3DTypeNames();
        Assert.Equal("Type B", vm.Selected3DTypeName);
    }

    [Fact]
    public void Populate3DTypeNames_FallsBackToFirstType_WhenPreviousMissing()
    {
        var (vm, _, _) = MakeVm();
        vm.Model3DAssets = new ObservableCollection<FamilyAsset>(
            new[] { MakeModel3DAsset("id-1", "Type A") });

        vm.Populate3DTypeNames();
        Assert.Equal("Type A", vm.Selected3DTypeName);

        // Refresh with completely different types.
        vm.Model3DAssets = new ObservableCollection<FamilyAsset>(
            new[] { MakeModel3DAsset("id-2", "Type C") });

        vm.Populate3DTypeNames();
        Assert.Equal("Type C", vm.Selected3DTypeName);
    }

    [Fact]
    public void Selected3DTypeName_Setter_DoesNotTriggerPreviewLoad()
    {
        var (vm, assetService, geometryPipeline) = MakeVm();

        vm.Selected3DTypeName = "Type A";

        geometryPipeline.Verify(
            x => x.RunAsync(
                It.IsAny<IReadOnlyList<FamilyGeometryPerType>?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        assetService.Verify(
            x => x.ResolveAssetPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChangeSelected3DTypeCommand_Null_KeepsCurrentSelection()
    {
        var (vm, _, _) = MakeVm();
        vm.Selected3DTypeName = "Type A";

        await vm.ChangeSelected3DTypeCommand.ExecuteAsync(null);

        Assert.Equal("Type A", vm.Selected3DTypeName);
    }

    [Fact]
    public async Task ChangeSelected3DTypeCommand_SameType_KeepsCurrentSelection()
    {
        var (vm, _, _) = MakeVm();
        vm.Selected3DTypeName = "Type A";

        await vm.ChangeSelected3DTypeCommand.ExecuteAsync("Type A");

        Assert.Equal("Type A", vm.Selected3DTypeName);
    }

    [Fact]
    public async Task Load3DPreviewForTypeAsync_NullType_DoesNotTriggerExtraction()
    {
        var (vm, assetService, geometryPipeline) = MakeVm();

        // Bypass the EffectsManager3D guard so the null-type guard is exercised.
        var effectsManager = Mock.Of<IEffectsManager>();
        typeof(FamilyPropertiesViewModel).GetProperty(nameof(FamilyPropertiesViewModel.EffectsManager3D))!
            .SetValue(vm, effectsManager);

        await vm.Load3DPreviewForTypeAsync(null, CancellationToken.None);

        geometryPipeline.Verify(
            x => x.RunAsync(
                It.IsAny<IReadOnlyList<FamilyGeometryPerType>?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        assetService.Verify(
            x => x.ResolveAssetPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Load3DPreviewForTypeAsync_WithEffectsManagerNull_DoesNothing()
    {
        var (vm, assetService, geometryPipeline) = MakeVm();

        Assert.Null(vm.EffectsManager3D);
        await vm.Load3DPreviewForTypeAsync("Type A", CancellationToken.None);

        geometryPipeline.Verify(
            x => x.RunAsync(
                It.IsAny<IReadOnlyList<FamilyGeometryPerType>?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        assetService.Verify(
            x => x.ResolveAssetPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadFactsAsync_RuleCategoryWithFact_AddsLocalizedRow()
    {
        var (vm, _, _) = MakeVm(new FamilyFactsData(
            -2008049, // OST_PipeFitting
            [new FamilyFact("part_type", "5", "Elbow")]));

        await vm.LoadFactsAsync(CancellationToken.None);

        var row = Assert.Single(vm.FactRows);
        Assert.Equal("Тип детали", row.Label);
        Assert.Equal("Отвод", row.Value);
        Assert.True(vm.HasFactRows);
    }

    [Fact]
    public async Task LoadFactsAsync_UndefinedPartType_ShowsUndefinedLabel()
    {
        var (vm, _, _) = MakeVm(new FamilyFactsData(
            -2008049,
            [new FamilyFact("part_type", "-1", "Undefined")]));

        await vm.LoadFactsAsync(CancellationToken.None);

        var row = Assert.Single(vm.FactRows);
        Assert.Equal("Не определён", row.Value);
        Assert.True(vm.HasFactRows);
    }

    [Fact]
    public async Task LoadFactsAsync_SentinelFact_HidesBlock()
    {
        var (vm, _, _) = MakeVm(new FamilyFactsData(
            -2008049,
            [new FamilyFact("part_type", "", "")]));

        await vm.LoadFactsAsync(CancellationToken.None);

        Assert.Empty(vm.FactRows);
        Assert.False(vm.HasFactRows);
    }

    [Fact]
    public async Task LoadFactsAsync_NullCategoryId_HidesBlock()
    {
        var (vm, _, _) = MakeVm(new FamilyFactsData(null, []));

        await vm.LoadFactsAsync(CancellationToken.None);

        Assert.Empty(vm.FactRows);
        Assert.False(vm.HasFactRows);
    }

    [Fact]
    public async Task LoadFactsAsync_CategoryWithoutRules_HidesBlock()
    {
        var (vm, _, _) = MakeVm(new FamilyFactsData(-2008044, [])); // OST_PipeCurves

        await vm.LoadFactsAsync(CancellationToken.None);

        Assert.Empty(vm.FactRows);
        Assert.False(vm.HasFactRows);
    }
}
