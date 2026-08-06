using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #183: system-type sync matches by (family, name) — never by name
/// alone. Both conduit families ("with/without Fittings") carry a type named
/// "Стандарт"; syncing one must never touch the other, and a created type
/// must be duplicated from a prototype of the SAME family.
/// </summary>
public sealed class SystemTypeFamilyIdentityTests : RevitApiTest
{
    private const string SeedTypeName = "SC_Identity";
    private const string CreateOnlyTypeName = "SC_IdentityCreated";
    private const string CatalogItemId = "identity-test-item";
    private const string VersionLabel = "v1";

    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _sourceTx;
    private RevitTransactionService? _targetTx;
    private SystemTypeSyncService? _syncService;
    private RevitSystemTypeFinder? _finder;
    private string? _sourceFamilyName;
    private string? _otherFamilyName;

    private Document SourceDoc => _sourceDoc!;
    private Document TargetDoc => _targetDoc!;
    private SystemTypeSyncService SyncService => _syncService!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        // MEP-шаблон — содержит ConduitType (дефолтный Metric-шаблон их не имеет).
        _sourceDoc = SampleFiles.NewMepTemplateDocument(Application);
        _targetDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (_sourceDoc is null || _targetDoc is null)
        {
            Skip.Test("Нет MEP-шаблона с ConduitType");
        }

        _sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        _targetTx = new RevitTransactionService(new StubRevitContext(TargetDoc));
        _finder = new RevitSystemTypeFinder();
        var materialSync = new RevitMaterialSyncService();
        var segmentSync = new RevitSegmentSyncService(materialSync);
        _syncService = new SystemTypeSyncService(
            _targetTx, new RevitFamilySnapshotExtractor(), _finder, new SystemClock(),
            materialSync, segmentSync, new NullFittingDependencyResolver(),
            new RevitCompoundStructureSyncService(materialSync));

        // Зонд: состав conduit-семей в шаблоне (локализация имён неизвестна).
        var conduitFamilies = new FilteredElementCollector(TargetDoc)
            .OfClass(typeof(ConduitType)).Cast<ElementType>()
            .Select(t => t.FamilyName)
            .Distinct()
            .ToList();
        SmartConLogger.Info($"PROBE #183: conduit families in template = [{string.Join(" | ", conduitFamilies)}]");
        if (conduitFamilies.Count < 2)
        {
            Skip.Test("Шаблон содержит меньше двух conduit-семей — контракт #183 не проверить");
        }

        _sourceTx!.RunInTransaction(SourceDoc, "Seed reference conduit type", doc =>
        {
            // Эталон: Duplicate ПЕРВОЙ семьи (какая именно — неважно, имя берём из документа).
            var prototype = new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType)).Cast<ConduitType>().First();
            var seed = (ElementType)prototype.Duplicate(SeedTypeName);
            seed.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("ref-comment");
            _sourceFamilyName = seed.FamilyName;

            // Тип, существующий ТОЛЬКО в эталоне — для контракта создания.
            var createOnly = (ElementType)prototype.Duplicate(CreateOnlyTypeName);
            createOnly.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("ref-created");
        });

        _targetTx!.RunInTransaction(TargetDoc, "Seed same-name types in BOTH families", doc =>
        {
            // Тип с тем же именем в СВОЕЙ семье (будет обновлён sync'ом)…
            var sameFamilyPrototype = new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType)).Cast<ConduitType>()
                .First(t => t.FamilyName == _sourceFamilyName);
            var sameFamilyType = (ElementType)sameFamilyPrototype.Duplicate(SeedTypeName);
            sameFamilyType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("same-family-original");

            // …и в ЧУЖОЙ семье (НЕ должен быть тронут).
            var otherFamilyPrototype = new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType)).Cast<ConduitType>()
                .First(t => t.FamilyName != _sourceFamilyName);
            _otherFamilyName = otherFamilyPrototype.FamilyName;
            var otherFamilyType = (ElementType)otherFamilyPrototype.Duplicate(SeedTypeName);
            otherFamilyType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("other-family-original");
        });
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetDoc?.Close(false);
    }

    [Test]
    public async Task Sync_ExistingSameNameInBothFamilies_UpdatesOnlySameFamily()
    {
        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, SeedTypeName, CatalogItemId, VersionLabel,
            int.Parse(Application.VersionNumber), _sourceFamilyName);

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Updated);

            var sameFamilyTypeId = _finder!.FindTypeByName(
                TargetDoc, SeedTypeName, null, _sourceFamilyName);
            var otherFamilyTypeId = _finder.FindTypeByName(
                TargetDoc, SeedTypeName, null, _otherFamilyName);

            await Assert.That(sameFamilyTypeId).IsNotNull();
            await Assert.That(otherFamilyTypeId).IsNotNull();

            var sameComment = (TargetDoc.GetElement(sameFamilyTypeId!) as ElementType)
                ?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString();
            var otherComment = (TargetDoc.GetElement(otherFamilyTypeId!) as ElementType)
                ?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString();

            // Своя семья — перезаписана эталоном; чужая — нетронута.
            await Assert.That(sameComment).IsEqualTo("ref-comment");
            await Assert.That(otherComment).IsEqualTo("other-family-original");
        }
    }

    [Test]
    public async Task Sync_NewType_CreatesInSameFamily_NotForeign()
    {
        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, CreateOnlyTypeName, CatalogItemId, VersionLabel,
            int.Parse(Application.VersionNumber), _sourceFamilyName);

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Created);

            var createdId = _finder!.FindTypeByName(
                TargetDoc, CreateOnlyTypeName, null, _sourceFamilyName);
            await Assert.That(createdId).IsNotNull();

            var created = TargetDoc.GetElement(createdId!) as ElementType;
            // Duplicate-прототип обязан быть из СВОЕЙ семьи — иначе тип
            // регистрируется в чужой семье (баг #183).
            await Assert.That(created!.FamilyName).IsEqualTo(_sourceFamilyName);

            var comment = created
                .get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString();
            await Assert.That(comment).IsEqualTo("ref-created");

            // В чужой семье тип с таким именем НЕ появился.
            var foreignId = _finder.FindTypeByName(
                TargetDoc, CreateOnlyTypeName, null, _otherFamilyName);
            await Assert.That(foreignId).IsNull();
        }
    }

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }
}
