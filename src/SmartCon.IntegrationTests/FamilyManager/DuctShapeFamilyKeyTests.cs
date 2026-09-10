using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #215 (FHV7): the "Воздуховоды" category has THREE system families
/// (round/rectangular/oval), discriminated by <c>MEPCurveType.Shape</c>.
/// A round reference synced into a project whose prototypes are rectangular
/// must NOT produce a rectangular type (pre-fix: the shared "Single" key
/// matched the rectangular prototype and the sync created a wrong-shape
/// type with UI-incompatible fittings).
/// </summary>
public sealed class DuctShapeFamilyKeyTests : RevitApiTest
{
    private const string RefTypeName = "SC_RoundRef";
    private const string CatalogItemId = "duct-shape-test-item";
    private const string VersionLabel = "v1";

    private Document? _sourceDoc;
    private Document? _targetMepDoc;
    private RevitTransactionService? _targetMepTx;
    private RevitSystemTypeFinder? _finder;
    private string? _roundFamilyName;
    private string? _rectFamilyName;

    private Document SourceDoc => _sourceDoc!;
    private Document TargetMepDoc => _targetMepDoc!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = SampleFiles.NewMepTemplateDocument(Application);
        _targetMepDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (_sourceDoc is null || _targetMepDoc is null)
        {
            Skip.Test("Нет MEP-шаблона с DuctType обеих сечений");
            return;
        }

        _finder = new RevitSystemTypeFinder();

        var shapesInSource = new FilteredElementCollector(SourceDoc)
            .OfClass(typeof(DuctType)).Cast<DuctType>()
            .Select(t => t.Shape)
            .Distinct()
            .ToList();
        if (!shapesInSource.Contains(ConnectorProfileType.Round)
            || !shapesInSource.Contains(ConnectorProfileType.Rectangular))
        {
            Skip.Test("Шаблон не содержит круглые и прямоугольные DuctType");
            return;
        }

        var sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        sourceTx.RunInTransaction(SourceDoc, "Seed round reference", doc =>
        {
            var roundPrototype = new FilteredElementCollector(doc)
                .OfClass(typeof(DuctType)).Cast<DuctType>()
                .First(t => t.Shape == ConnectorProfileType.Round);
            var seed = (ElementType)roundPrototype.Duplicate(RefTypeName);
            seed.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("ref-round");
            _roundFamilyName = seed.FamilyName;
        });

        _rectFamilyName = new FilteredElementCollector(SourceDoc)
            .OfClass(typeof(DuctType)).Cast<DuctType>()
            .First(t => t.Shape == ConnectorProfileType.Rectangular)
            .FamilyName;

        _targetMepTx = new RevitTransactionService(new StubRevitContext(TargetMepDoc));
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetMepDoc?.Close(false);
    }

    [Test]
    public async Task Resolver_DuctTypes_ResolveShapeKeys()
    {
        var keysByShape = new FilteredElementCollector(SourceDoc)
            .OfClass(typeof(DuctType)).Cast<DuctType>()
            .GroupBy(t => t.Shape)
            .ToDictionary(g => g.Key, g => SystemFamilyKeyResolver.Resolve(g.First()));

        using (Assert.Multiple())
        {
            await Assert.That(keysByShape[ConnectorProfileType.Round])
                .IsEqualTo(Core.Models.FamilyManager.SystemFamilyKeys.DuctRound);
            await Assert.That(keysByShape[ConnectorProfileType.Rectangular])
                .IsEqualTo(Core.Models.FamilyManager.SystemFamilyKeys.DuctRectangular);
            await Assert.That(keysByShape[ConnectorProfileType.Round])
                .IsNotEqualTo(keysByShape[ConnectorProfileType.Rectangular]);
        }
    }

    [Test]
    public async Task Sync_RoundReference_CreatesRoundType_NotRectangular()
    {
        var syncService = BuildSyncService(_targetMepTx!, TargetMepDoc);

        var result = syncService.SyncTypeFromSource(
            SourceDoc, TargetMepDoc, RefTypeName, CatalogItemId, VersionLabel,
            int.Parse(Application.VersionNumber), _roundFamilyName);

        await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Created);

        var createdId = _finder!.FindTypeByName(
            TargetMepDoc, RefTypeName, null, _roundFamilyName);
        await Assert.That(createdId).IsNotNull();

        var created = (DuctType)TargetMepDoc.GetElement(createdId!)!;
        using (Assert.Multiple())
        {
            await Assert.That(created.FamilyName).IsEqualTo(_roundFamilyName);
            await Assert.That(created.Shape).IsEqualTo(ConnectorProfileType.Round);
            // В прямоугольной семье тип с таким именем НЕ появился.
            var foreignId = _finder.FindTypeByName(
                TargetMepDoc, RefTypeName, null, _rectFamilyName);
            await Assert.That(foreignId).IsNull();
        }
    }

    [Test]
    public async Task Finder_RoundKey_NeverMatchesRectangularType()
    {
        // Детерминированная замена сценария «нет круглого прототипа» (тот
        // недостижим в шаблонах: последний тип системной семьи неудаляем, а
        // дефолтный шаблон содержит круглый DuctType — skip-guard срабатывал
        // всегда). Контракт #215 на уровне finder'а: ключевой фильтр круглого
        // сечения обязан ВЕТИРОВАТЬ одноимённый прямоугольный тип — именно
        // этот механизм до фикса подбирал прямоугольный прототип по «Single».
        var rectType = new FilteredElementCollector(TargetMepDoc)
            .OfClass(typeof(DuctType)).Cast<DuctType>()
            .First(t => t.Shape == ConnectorProfileType.Rectangular);

        var byRoundKey = _finder!.FindTypeByName(
            TargetMepDoc, rectType.Name, (int)BuiltInCategory.OST_DuctCurves,
            familyKey: Core.Models.FamilyManager.SystemFamilyKeys.DuctRound);
        var byRectKey = _finder.FindTypeByName(
            TargetMepDoc, rectType.Name, (int)BuiltInCategory.OST_DuctCurves,
            familyKey: Core.Models.FamilyManager.SystemFamilyKeys.DuctRectangular);
        var byRoundFamilyName = _finder.FindTypeByName(
            TargetMepDoc, rectType.Name, (int)BuiltInCategory.OST_DuctCurves,
            familyName: _roundFamilyName);

        using (Assert.Multiple())
        {
            await Assert.That(byRoundKey).IsNull();
            await Assert.That(byRoundFamilyName).IsNull();
            await Assert.That(byRectKey).IsEqualTo(rectType.Id);
        }
    }

    private SystemTypeSyncService BuildSyncService(RevitTransactionService tx, Document target)
    {
        var materialSync = new RevitMaterialSyncService();
        return new SystemTypeSyncService(
            tx, new RevitFamilySnapshotExtractor(), _finder!, new SystemClock(),
            materialSync, new RevitSegmentSyncService(materialSync),
            new NullFittingDependencyResolver(),
            new RevitCompoundStructureSyncService(materialSync));
    }

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }
}
