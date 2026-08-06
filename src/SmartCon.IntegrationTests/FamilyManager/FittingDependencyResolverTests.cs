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
/// ADR-066 (E1, #208): routing sync resolves fitting dependencies through
/// <c>family_dependencies</c> links (link-first, name lookup only as the
/// legacy fallback) and stamps the loaded fitting with the ES version
/// marker — the fitting becomes a full loadable-lifecycle participant.
/// Fakes back the catalog/DB seam (SQL is unit-tested in SmartCon.Tests);
/// everything Revit-boundary (sync, LoadFamily, ES marker) is real.
/// </summary>
public sealed class FittingDependencyResolverTests : RevitApiTest
{
    private const string TypeName = "SmartCon Link Pipe";
    private const string ParentItemId = "parent-item-link-1";
    private const string ChildItemId = "child-fitting-1";
    private const string VersionLabel = "v1";
    private const string ExtraTypeName = "SmartCon Extra Type";

    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _targetTx;
    private RevitSystemTypeFinder? _finder;
    private RevitFamilyVersionStore? _versionStore;
    private FakeDependencyRepository? _dependencyRepository;
    private FakeCatalogProvider? _catalog;
    private string? _tempDir;
    private string? _fittingFamilyName;
    private string? _fittingTypeName;

    private Document SourceDoc => _sourceDoc!;
    private Document TargetDoc => _targetDoc!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (_sourceDoc is null)
        {
            Skip.Test("MEP-шаблон не найден — сидирование PipeType с фитингом невозможно");
            return;
        }

        _targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _targetTx = new RevitTransactionService(new StubRevitContext(TargetDoc));
        _finder = new RevitSystemTypeFinder();
        _versionStore = new RevitFamilyVersionStore(_targetTx);
        _dependencyRepository = new FakeDependencyRepository();
        _catalog = new FakeCatalogProvider();

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConFittingDep_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        ElementId? fittingSymbolId = null;
        var attemptErrors = new List<string>();
        var sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        sourceTx.RunInTransaction(SourceDoc, "Seed reference routing with fitting", doc =>
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();
            if (pipeType is null)
            {
                attemptErrors.Add("PipeType not found in the MEP template");
                return;
            }

            var pipeFittingCategoryId = new ElementId(BuiltInCategory.OST_PipeFitting);
            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s =>
                    s.Family is not null &&
                    s.Family.IsEditable &&
                    s.Category is not null &&
                    s.Category.Id == pipeFittingCategoryId)
                .ToList();

            var type = (MEPCurveType)pipeType.Duplicate(TypeName);
            using (var manager = type.RoutingPreferenceManager)
            {
                foreach (RoutingPreferenceRuleGroupType group in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
                {
                    if (group == RoutingPreferenceRuleGroupType.Undefined) continue;
                    for (var i = manager.GetNumberOfRules(group) - 1; i >= 0; i--)
                    {
                        try { manager.RemoveRule(group, i); }
                        catch { /* informational — некоторые группы защищены */ }
                    }
                }

                foreach (var candidate in candidates)
                {
                    try
                    {
                        manager.AddRule(
                            RoutingPreferenceRuleGroupType.Elbows,
                            new RoutingPreferenceRule(candidate.Id, "reference elbow"));
                        fittingSymbolId = candidate.Id;
                        break;
                    }
                    catch (Exception ex)
                    {
                        attemptErrors.Add($"{candidate.Family!.Name}:{candidate.Name} — {ex.Message}");
                    }
                }
            }

            if (fittingSymbolId is not null)
            {
                var symbol = (FamilySymbol)doc.GetElement(fittingSymbolId)!;
                _fittingFamilyName = symbol.Family!.Name;
                _fittingTypeName = symbol.Name;
            }
        });

        if (fittingSymbolId is null)
        {
            Skip.Test(
                "В MEP-шаблоне нет редактируемого pipe-fitting семейства для Elbows-правила. " +
                $"Попыток: {attemptErrors.Count}. Первые: {string.Join(" | ", attemptErrors.Take(3))}");
            return;
        }

        // EditFamily requires a non-modifiable document — it must run AFTER
        // the seed transaction has committed (FittingFamilyRepository
        // precedent: EditFamily works only when doc.IsModifiable == false).
        var fittingSymbol = (FamilySymbol)SourceDoc.GetElement(fittingSymbolId)!;
        var familyDoc = SourceDoc.EditFamily(fittingSymbol.Family!);
        try
        {
            // #212: the exported family must carry ≥2 types so the per-type
            // load assertion is meaningful (full-family load would bring both,
            // LoadFamilySymbol only the rule-referenced one).
            try
            {
                using var tx = new Transaction(familyDoc, "Add extra type");
                tx.Start();
                familyDoc.FamilyManager.NewType(ExtraTypeName);
                tx.Commit();
            }
            catch
            {
                // Informational — семейство может запрещать NewType; тогда
                // per-type assert проверяется по существующим типам.
            }

            familyDoc.SaveAs(
                Path.Combine(_tempDir, $"{_fittingFamilyName}.rfa"),
                new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally
        {
            familyDoc.Close(false);
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetDoc?.Close(false);
        try
        {
            if (_tempDir is not null && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Test]
    public async Task Sync_FittingResolvedViaDependencyLink_LoadedAndMarked()
    {
        // Каталог НЕ знает фитинг по имени — только связь может его резолвить.
        _catalog!.ChildItem = MakeChildItem();
        _dependencyRepository!.LinksByParent[ParentItemId] = new List<FamilyDependencyInfo>
        {
            new(ChildItemId, FamilyDependencyKind.Routing, $"{_fittingFamilyName}:{_fittingTypeName}", 0),
        };
        var syncService = BuildSyncService();

        var result = syncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, ParentItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.NotConvergedCount).IsEqualTo(0);
        }

        var symbol = FindFittingSymbol(TargetDoc);
        await Assert.That(symbol).IsNotNull();

        // #212: per-type loading — в проекте ТОЛЬКО тип из routing-правила,
        // а не все типы семейства (в экспортированном .rfa есть и
        // SmartCon Extra Type).
        var loadedSymbolsOfFamily = new FilteredElementCollector(TargetDoc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => string.Equals(s.Family?.Name, _fittingFamilyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        using (Assert.Multiple())
        {
            await Assert.That(loadedSymbolsOfFamily.Count).IsEqualTo(1);
            await Assert.That(loadedSymbolsOfFamily[0].Name).IsEqualTo(_fittingTypeName!);
        }

        var marker = _versionStore!.ReadFromLoadedFamily(TargetDoc, symbol!.Family.Id);
        using (Assert.Multiple())
        {
            await Assert.That(marker).IsNotNull();
            await Assert.That(marker!.CatalogItemId).IsEqualTo(ChildItemId);
            await Assert.That(marker.VersionLabel).IsEqualTo(VersionLabel);
        }
    }

    [Test]
    public async Task Sync_NoDependencyLink_FallsBackToNameLookup()
    {
        // Эталон до фичи (связей нет) — фитинг резолвится по имени, как раньше.
        _catalog!.ChildItem = MakeChildItem();
        _catalog.ReturnByName = true;
        var syncService = BuildSyncService();

        var result = syncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, ParentItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        await Assert.That(result.IsSuccess).IsTrue();
        var symbol = FindFittingSymbol(TargetDoc);
        await Assert.That(symbol).IsNotNull();
    }

    [Test]
    public async Task Sync_TypeAbsentFromCatalogVersion_FullFamilyFallback()
    {
        // #212 (F5): тип правила отсутствует в типах каталога (typeless-файл
        // или переименованный в проекте тип) → fallback на полный LoadFamily —
        // в проект попадают ОБА типа семейства (консервативный путь, который
        // гарантированно приносит нужный контент).
        _catalog!.ChildItem = MakeChildItem();
        _dependencyRepository!.LinksByParent[ParentItemId] = new List<FamilyDependencyInfo>
        {
            new(ChildItemId, FamilyDependencyKind.Routing, $"{_fittingFamilyName}:{_fittingTypeName}", 0),
        };
        var syncService = BuildSyncService(catalogTypeNames: []);

        var result = syncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, ParentItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        await Assert.That(result.IsSuccess).IsTrue();
        var loadedSymbolsOfFamily = new FilteredElementCollector(TargetDoc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => string.Equals(s.Family?.Name, _fittingFamilyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        using (Assert.Multiple())
        {
            await Assert.That(loadedSymbolsOfFamily.Count).IsGreaterThanOrEqualTo(2);
            await Assert.That(loadedSymbolsOfFamily.Any(
                s => string.Equals(s.Name, _fittingTypeName, StringComparison.OrdinalIgnoreCase))).IsTrue();
        }
    }

    [Test]
    public async Task Sync_NonPipeFitting_FullFamilyLoad()
    {
        // #216: duct-стиль диалога трассировки индексирует части только на
        // реальной полной загрузке семейства — НЕ-pipe фитинг обязан идти
        // полным LoadFamily, даже когда тип правила есть в типах версии
        // (иначе диалог показывает пустые строки + «НЕТ»). В проект попадают
        // ОБА типа (в .rfa есть и SmartCon Extra Type).
        _catalog!.ChildItem = MakeChildItem((int)BuiltInCategory.OST_DuctFitting);
        _dependencyRepository!.LinksByParent[ParentItemId] = new List<FamilyDependencyInfo>
        {
            new(ChildItemId, FamilyDependencyKind.Routing, $"{_fittingFamilyName}:{_fittingTypeName}", 0),
        };
        var syncService = BuildSyncService();

        var result = syncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, ParentItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        await Assert.That(result.IsSuccess).IsTrue();
        var loadedSymbolsOfFamily = new FilteredElementCollector(TargetDoc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => string.Equals(s.Family?.Name, _fittingFamilyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        using (Assert.Multiple())
        {
            await Assert.That(loadedSymbolsOfFamily.Count).IsGreaterThanOrEqualTo(2);
            await Assert.That(loadedSymbolsOfFamily.Any(
                s => string.Equals(s.Name, _fittingTypeName, StringComparison.OrdinalIgnoreCase))).IsTrue();
        }
    }

    [Test]
    public async Task Sync_FittingUnknownEverywhere_RuleSkippedHonestly()
    {
        // Ни связи, ни имени в каталоге — правило пропускается с NotConverged,
        // синк типа при этом завершается успешно (честный failure-путь).
        var syncService = BuildSyncService();

        var result = syncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, ParentItemId, VersionLabel,
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.NotConvergedCount).IsGreaterThan(0);
        }
    }

    private SystemTypeSyncService BuildSyncService(string[]? catalogTypeNames = null)
    {
        var materialSync = new RevitMaterialSyncService();
        var fileResolver = new FakeFileResolver(_tempDir!);
        var loadService = new RevitFamilyLoadService(new StubRevitContext(TargetDoc), _targetTx!);
        var fittingResolver = new CatalogFittingDependencyResolver(
            _catalog!, fileResolver, loadService, _dependencyRepository!,
            new FakeTypeRepository(catalogTypeNames ?? [_fittingTypeName!, ExtraTypeName]),
            _versionStore!, new SystemClock());
        return new SystemTypeSyncService(
            _targetTx!, new RevitFamilySnapshotExtractor(), _finder!, new SystemClock(),
            materialSync, new RevitSegmentSyncService(materialSync), fittingResolver,
            new RevitCompoundStructureSyncService(materialSync));
    }

    private FamilyCatalogItem MakeChildItem(int? revitCategoryId = (int)BuiltInCategory.OST_PipeFitting) =>
        new(
            Id: ChildItemId,
            Name: _fittingFamilyName!,
            NormalizedName: _fittingFamilyName!.ToUpperInvariant(),
            Description: null,
            CategoryPath: null,
            CategoryId: null,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: VersionLabel,
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            RevitCategoryId: revitCategoryId);

    private FamilySymbol? FindFittingSymbol(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(s.Name, _fittingTypeName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Family?.Name, _fittingFamilyName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeDependencyRepository : IFamilyDependencyRepository
    {
        public Dictionary<string, List<FamilyDependencyInfo>> LinksByParent { get; } = new(StringComparer.Ordinal);

        public Task ReplaceForVersionAsync(
            string parentCatalogItemId, string parentVersionId,
            IReadOnlyList<FamilyDependencyInfo> dependencies, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<int> ReplaceForCurrentVersionAsync(
            string parentCatalogItemId, IReadOnlyList<FamilyDependencyInfo> dependencies,
            CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<FamilyDependencyInfo>> GetForCurrentVersionAsync(
            string parentCatalogItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FamilyDependencyInfo>>(
                LinksByParent.TryGetValue(parentCatalogItemId, out var links)
                    ? links
                    : Array.Empty<FamilyDependencyInfo>());
    }

    private sealed class FakeTypeRepository : IFamilyTypeRepository
    {
        private readonly IReadOnlyList<FamilyTypeDescriptor> _types;

        public FakeTypeRepository(params string[] typeNames)
        {
            _types = typeNames
                .Select(n => new FamilyTypeDescriptor(
                    Guid.NewGuid().ToString(), ChildItemId, n, 0, "version-row-1"))
                .ToList();
        }

        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(
            string catalogItemId, CancellationToken ct = default) =>
            Task.FromResult(_types);

        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(
            string catalogItemId, string? versionId, CancellationToken ct = default) =>
            Task.FromResult(_types);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(
            IEnumerable<string> catalogItemIds, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
            string catalogItemId, string? versionId, string? fileId, string runId,
            IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeCatalogProvider : IFamilyCatalogProvider
    {
        public FamilyCatalogItem? ChildItem { get; set; }
        public bool ReturnByName { get; set; }

        public Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ChildItem is not null && ChildItem.Id == id ? ChildItem : null);

        public Task<FamilyCatalogItem?> FindByNormalizedNameAsync(
            string normalizedName, string? familySource = null, CancellationToken ct = default) =>
            Task.FromResult(ReturnByName ? ChildItem : null);

        public FamilyCatalogCapabilities GetCapabilities() => throw new NotImplementedException();
        public Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(FamilyCatalogQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(string catalogItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FamilyCatalogVersion?> GetVersionByIdAsync(string catalogItemId, string versionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FamilyCatalogVersion?> GetVersionByLabelAsync(string catalogItemId, string versionLabel, int targetRevitMajorVersion = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetItemCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FamilyCatalogItem?> FindByRevitCategoryIdAsync(int revitCategoryId, string familySource, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(string hexHash, int hashFormatVersion, string familySource, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeFileResolver : IFamilyFileResolver
    {
        private readonly string _tempDir;

        public FakeFileResolver(string tempDir)
        {
            _tempDir = tempDir;
        }

        public Task<FamilyResolvedFile> ResolveForLoadAsync(
            string catalogItemId, int targetRevitVersion, CancellationToken ct = default)
        {
            var path = Directory.GetFiles(_tempDir, "*.rfa").Single();
            return Task.FromResult(new FamilyResolvedFile(path, catalogItemId, "version-row-1", "v1"));
        }

        public Task<FamilyResolvedFile> ResolveVersionAsync(
            string catalogItemId, string versionLabel, CancellationToken ct = default) =>
            ResolveForLoadAsync(catalogItemId, 0, ct);

        public string? GetDatabaseRoot() => _tempDir;
    }
}
