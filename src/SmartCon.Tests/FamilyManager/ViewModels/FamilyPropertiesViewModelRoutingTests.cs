using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Routing-tab load behavior of <see cref="FamilyPropertiesViewModel"/>
/// (ADR-072): the dirty baseline must be captured AFTER the stored mixed
/// size pair (0..150) is normalized — merely opening the tab must never
/// mark the type edited (audit M2).
/// </summary>
public sealed class FamilyPropertiesViewModelRoutingTests
{
    [Fact]
    public async Task LoadRouting_MixedSizePair_NoPhantomDirty_AndStateNormalized()
    {
        var routingService = new Mock<IRoutingEditorService>();
        routingService
            .Setup(x => x.LoadAsync("catalog-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingEditorData(
                RoutingGroupCatalog.PipeCurvesCategoryId,
                [new RoutingEditorTypeData("Type A", "Single", "Pipe Types", true)],
                [new FamilyRoutingRuleInfo("Type A", "Single", "Elbows", 0, "A:B", "",
                    [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0, 150.0 / 304.8)])],
                [new FamilyRoutingTypeSettings("Type A", "Single", 0)],
                [],
                [50.0 / 304.8, 100.0 / 304.8, 150.0 / 304.8],
                [],
                new Dictionary<string, int>()));

        var vm = MakeVm(routingService.Object);
        await vm.InitializeCommand.ExecuteAsync(null);

        // No phantom dirty: the baseline fingerprint was taken on the
        // already-normalized state.
        Assert.False(vm.HasRoutingChanges);
        Assert.False(vm.HasUnsavedChanges);

        // The displayed state IS the normalized one: the unbounded min
        // reads as the smallest available nominal, not «Все».
        var rule = vm.RoutingGroups
            .SelectMany(g => g.State.Rules)
            .Single(r => r.PartName == "A:B");
        Assert.Equal("50", rule.MinSizeText);
        Assert.Equal("150", rule.MaxSizeText);
    }

    /// <summary>
    /// Per-FAMILY host connector-shape mapping (owner requirement 2026-09-01):
    /// the duct category holds Round/Rectangular/Oval families with their own
    /// types — the picker filter must follow the selected type's family_key,
    /// never the category. A rectangular duct must never offer round parts.
    /// </summary>
    [Theory]
    [InlineData(SystemFamilyKeys.DuctRound, 1)]
    [InlineData(SystemFamilyKeys.FlexDuctRound, 1)]
    [InlineData(SystemFamilyKeys.DuctRectangular, 2)]
    [InlineData(SystemFamilyKeys.FlexDuctRectangular, 2)]
    [InlineData(SystemFamilyKeys.DuctOval, 4)]
    [InlineData("Single", 0)] // pipes/unknown keys — no shape filter (legacy degrade)
    public void HostConnectorShapeBits_PerFamilyKey_MapsShapeFilter(string familyKey, int expectedBits)
    {
        Assert.Equal(expectedBits, FamilyPropertiesViewModel.HostConnectorShapeBits(familyKey));
    }

    private static FamilyPropertiesViewModel MakeVm(IRoutingEditorService routingEditorService)
    {
        var assetService = new Mock<IFamilyAssetService>();
        assetService
            .Setup(x => x.GetAssetsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FamilyAsset>());
        var factRepository = new Mock<IFamilyFactRepository>();
        factRepository
            .Setup(x => x.GetForItemAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FamilyFactsData(null, []));

        return new FamilyPropertiesViewModel(
            catalogItemId: "catalog-1",
            name: "Test Pipes",
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
            geometryPipeline: new Mock<IFamilyGeometryPipeline>().Object,
            fileResolver: new Mock<IFamilyFileResolver>().Object,
            avatarCropService: new Mock<IAvatarCropService>().Object,
            updateState: new FakeDatabaseUpdateStateService(),
            factRepository: factRepository.Object,
            categoryChangeGate: new Mock<ICategoryChangeGateService>().Object,
            familySource: "system",
            revitCategoryId: RoutingGroupCatalog.PipeCurvesCategoryId,
            routingEditorService: routingEditorService);
    }
}
