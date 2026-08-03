using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
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
/// SystemTypeSyncService + SystemFamilySyncOrchestrator (Issue #104):
/// синхронизация системного типа из мини-проекта в активный проект без
/// копирования элементов — создание дубликатом болванки, перезапись
/// параметров существующего типа, ES-маркер на ElementType, отсутствие
/// дубликатов типов.
/// </summary>
public sealed class SystemTypeSyncTests : RevitApiTest
{
    private const string TestTypeName = "SmartCon Sync Test Pipe";
    private const string ReferenceComment = "reference-comment";
    private const string CatalogItemId = "sync-test-item";
    private const string VersionLabel = "v7";

    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _sourceTx;
    private RevitTransactionService? _targetTx;
    private RevitFamilyVersionStore? _store;
    private SystemTypeSyncService? _syncService;
    private RevitSystemTypeFinder? _finder;
    private RevitMaterialSyncService? _materialSync;
    private RevitSegmentSyncService? _segmentSync;
    private ElementId? _sourceTypeId;
    private string? _doubleParamName;
    private double _doubleParamValue;

    private Document SourceDoc => _sourceDoc!;
    private Document TargetDoc => _targetDoc!;
    private SystemTypeSyncService SyncService => _syncService!;
    private RevitFamilyVersionStore Store => _store!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        _targetTx = new RevitTransactionService(new StubRevitContext(TargetDoc));
        _store = new RevitFamilyVersionStore(_targetTx);
        _finder = new RevitSystemTypeFinder();
        var materialSync = new RevitMaterialSyncService();
        var segmentSync = new RevitSegmentSyncService(materialSync);
        _materialSync = materialSync;
        _segmentSync = segmentSync;
        _syncService = new SystemTypeSyncService(
            _targetTx, new RevitFamilySnapshotExtractor(), _finder, new SystemClock(),
            materialSync, segmentSync, new NullFittingDependencyResolver(),
            new RevitCompoundStructureSyncService(materialSync));

        _sourceTx.RunInTransaction(SourceDoc, "Seed reference pipe type", doc =>
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();
            if (pipeType is null) return;

            var seed = (ElementType)pipeType.Duplicate(TestTypeName);
            var comment = seed.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
            comment?.Set(ReferenceComment);

            // Type Mark в эталоне должен быть пуст — тест
            // Sync_ExistingType_ClearsParameterAbsentInReference проверяет,
            // что локальное значение в проекте сбрасывается эталоном.
            var mark = seed.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            if (mark is { HasValue: true, IsReadOnly: false })
            {
                mark.ClearValue();
            }

            // Найти любой writable double-параметр для проверки числовой записи.
            foreach (Parameter p in seed.Parameters)
            {
                if (p.IsReadOnly || p.StorageType != StorageType.Double || !p.HasValue) continue;
                _doubleParamName = p.Definition?.Name;
                _doubleParamValue = p.AsDouble() + 0.125;
                p.Set(_doubleParamValue);
                break;
            }

            _sourceTypeId = seed.Id;
        });

        if (_sourceTypeId is null)
        {
            Skip.Test("В шаблоне проекта нет PipeType — сидирование невозможно");
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetDoc?.Close(false);
    }

    [Test]
    public async Task Sync_NewType_CreatesTypeWithReferenceParametersAndMarker()
    {
        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TestTypeName, CatalogItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Created);
            await Assert.That(result.IsSuccess).IsTrue();
        }

        var targetId = _finder!.FindTypeByName(TargetDoc, TestTypeName, null);
        await Assert.That(targetId).IsNotNull();

        var target = (ElementType)TargetDoc.GetElement(targetId!)!;
        using (Assert.Multiple())
        {
            await Assert.That(
                target.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString())
                .IsEqualTo(ReferenceComment);

            if (_doubleParamName is not null)
            {
                await Assert.That(target.LookupParameter(_doubleParamName)?.AsDouble())
                    .IsEqualTo(_doubleParamValue);
            }

            var marker = Store.ReadFromType(TargetDoc, targetId!);
            await Assert.That(marker).IsNotNull();
            await Assert.That(marker!.CatalogItemId).IsEqualTo(CatalogItemId);
            await Assert.That(marker.VersionLabel).IsEqualTo(VersionLabel);
        }
    }

    [Test]
    public async Task Sync_ExistingType_OverwritesParametersWithoutDuplicates()
    {
        ElementId existingId = null!;
        _targetTx!.RunInTransaction(TargetDoc, "Seed local type", doc =>
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .First();
            var local = (ElementType)pipeType.Duplicate(TestTypeName);
            local.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set("local-edit");
            existingId = local.Id;
        });

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TestTypeName, CatalogItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Updated);

            // Тот же ElementId — тип обновлён на месте, не пересоздан.
            var afterId = _finder!.FindTypeByName(TargetDoc, TestTypeName, null);
            await Assert.That(afterId).IsEqualTo(existingId);

            var target = (ElementType)TargetDoc.GetElement(existingId)!;
            await Assert.That(
                target.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString())
                .IsEqualTo(ReferenceComment);

            // Ноль дубликатов типов с этим именем.
            var count = new FilteredElementCollector(TargetDoc)
                .OfClass(typeof(PipeType))
                .Cast<ElementType>()
                .Count(t => string.Equals(t.Name, TestTypeName, StringComparison.OrdinalIgnoreCase));
            await Assert.That(count).IsEqualTo(1);

            var marker = Store.ReadFromType(TargetDoc, existingId);
            await Assert.That(marker?.VersionLabel).IsEqualTo(VersionLabel);
        }
    }

    [Test]
    public async Task Sync_ExistingType_ClearsParameterAbsentInReference()
    {
        ElementId existingId = null!;
        _targetTx!.RunInTransaction(TargetDoc, "Seed local type with Type Mark", doc =>
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .First();
            var local = (ElementType)pipeType.Duplicate(TestTypeName);
            local.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.Set("LOCAL-MARK");
            existingId = local.Id;
        });

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TestTypeName, CatalogItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        await Assert.That(result.IsSuccess).IsTrue();

        // Ожидание выводится из фактического состояния эталона: если у
        // исходного типа Type Mark пуст — в проекте локальное значение
        // должно быть сброшено; если эталон хранит значение (например
        // параметр read-only и очистка в сиде невозможна) — оно переносится.
        var sourceMark = SourceDoc.GetElement(_sourceTypeId!)!
            .get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
        var target = (ElementType)TargetDoc.GetElement(existingId)!;
        var mark = target.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
        using (Assert.Multiple())
        {
            await Assert.That(mark).IsNotNull();
            if (sourceMark?.HasValue == true)
            {
                await Assert.That(mark!.AsString()).IsEqualTo(sourceMark.AsString());
            }
            else
            {
                // Эталон пуст — локальное значение сброшено (ClearValue для
                // shared-параметров, пустая строка для обычных).
                await Assert.That(string.IsNullOrEmpty(mark!.AsString())).IsTrue();
            }
        }
    }

    [Test]
    public async Task Sync_MissingSourceType_ReturnsNotFoundInSource()
    {
        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, "No Such Type", CatalogItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.NotFoundInSource);
            await Assert.That(_finder!.FindTypeByName(TargetDoc, "No Such Type", null)).IsNull();
        }
    }

    [Test]
    public async Task Orchestrator_SyncTypes_OpensMiniProjectSyncsAndStampsMarker()
    {
        // Self-contained: a dedicated mini-project document so the test does
        // not touch the shared fixture state (TUnit0018).
        var miniDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var miniPath = Path.Combine(
            Path.GetTempPath(), $"smartcon-synctest-{Guid.NewGuid():N}.rvt");
        try
        {
            var miniTx = new RevitTransactionService(new StubRevitContext(miniDoc));
            miniTx.RunInTransaction(miniDoc, "Seed reference pipe type", doc =>
            {
                var pipeType = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipeType))
                    .Cast<PipeType>()
                    .First();
                var seed = (ElementType)pipeType.Duplicate(TestTypeName);
                seed.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.Set(ReferenceComment);
            });
            miniDoc.SaveAs(miniPath);
            miniDoc.Close(false);
            miniDoc = null!;

            var resolver = new FakeFamilyFileResolver(miniPath, VersionLabel);
            var catalog = new FakeFamilyCatalogProvider();
            var orchestrator = new SystemFamilySyncOrchestrator(
                resolver, catalog, SyncService, _finder!, Store);

            var result = orchestrator.SyncTypes(
                TargetDoc, CatalogItemId, new[] { new SystemTypeRef(TestTypeName) },
                int.Parse(Application.VersionNumber));

            using (Assert.Multiple())
            {
                await Assert.That(result.AllSucceeded).IsTrue();

                var typeId = _finder!.FindTypeByName(TargetDoc, TestTypeName, null);
                await Assert.That(typeId).IsNotNull();

                // Fast-path: маркер актуален → тип считается текущим.
                var isCurrent = orchestrator.IsProjectTypeCurrent(
                    TargetDoc, CatalogItemId, TestTypeName,
                    int.Parse(Application.VersionNumber));
                await Assert.That(isCurrent).IsTrue();

                // Другая версия каталога → тип устарел.
                var staleResolver = new FakeFamilyFileResolver(miniPath, "v8");
                var staleOrchestrator = new SystemFamilySyncOrchestrator(
                    staleResolver, catalog, SyncService, _finder!, Store);
                var isStaleCurrent = staleOrchestrator.IsProjectTypeCurrent(
                    TargetDoc, CatalogItemId, TestTypeName,
                    int.Parse(Application.VersionNumber));
                await Assert.That(isStaleCurrent).IsFalse();
            }
        }
        finally
        {
            try { miniDoc?.Close(false); } catch { }
            try { File.Delete(miniPath); } catch { }
        }
    }

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion) => null;
    }

    private sealed class FakeFamilyFileResolver : IFamilyFileResolver
    {
        private readonly string _path;
        private readonly string _label;

        public FakeFamilyFileResolver(string path, string label)
        {
            _path = path;
            _label = label;
        }

        public Task<FamilyResolvedFile> ResolveForLoadAsync(
            string catalogItemId, int targetRevitVersion, CancellationToken ct = default)
        {
            return Task.FromResult(new FamilyResolvedFile(_path, catalogItemId, "version-1", _label));
        }

        public Task<FamilyResolvedFile> ResolveVersionAsync(
            string catalogItemId, string versionLabel, CancellationToken ct = default)
        {
            return Task.FromResult(new FamilyResolvedFile(_path, catalogItemId, "version-1", versionLabel));
        }

        public string? GetDatabaseRoot() => null;
    }

    private sealed class FakeFamilyCatalogProvider : IFamilyCatalogProvider
    {
        public FamilyCatalogCapabilities GetCapabilities() =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(
            FamilyCatalogQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<FamilyCatalogItem?>(null);

        public Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(
            string catalogItemId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<FamilyCatalogVersion?> GetVersionByIdAsync(
            string catalogItemId, string versionId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<FamilyCatalogVersion?> GetVersionByLabelAsync(
            string catalogItemId, string versionLabel, int targetRevitMajorVersion = 0, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> GetItemCountAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(
            string catalogItemId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<FamilyCatalogItem?> FindByNormalizedNameAsync(
            string normalizedName, string? familySource = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<FamilyCatalogItem?> FindByRevitCategoryIdAsync(
            int revitCategoryId, string familySource, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(
            string hexHash, int hashFormatVersion, string familySource, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(
            string familySource, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
