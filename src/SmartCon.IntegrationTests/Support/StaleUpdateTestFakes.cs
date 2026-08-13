using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// #209 fakes for <c>StaleUpdaterOrchestrationTests</c>: they let the real
/// internal <c>StaleFamilyUpdater</c> run inside the integration host with
/// full control over the catalog-side seams (file resolution, marker
/// writing) while the Revit-side seams stay REAL
/// (<c>RevitFamilyLoadService</c>, <c>RevitFamilyVersionStore</c>,
/// <c>RevitFamilySnapshotExtractor</c>). The awaitable event executes
/// INLINE — the test body already runs on the single Revit API thread
/// (assembly-level RevitThreadExecutor), so inline execution is the
/// correct marshalling here.
/// </summary>
internal sealed class StubFileResolver : IFamilyFileResolver
{
    private readonly string _path;
    private readonly string _versionLabel;

    public StubFileResolver(string path, string versionLabel)
    {
        _path = path;
        _versionLabel = versionLabel;
    }

    public Task<FamilyResolvedFile> ResolveForLoadAsync(
        string catalogItemId, int targetRevitVersion, CancellationToken ct = default)
    {
        return Task.FromResult(new FamilyResolvedFile(_path, catalogItemId, "version-id", _versionLabel));
    }

    public Task<FamilyResolvedFile> ResolveVersionAsync(
        string catalogItemId, string versionLabel, CancellationToken ct = default)
    {
        return Task.FromResult(new FamilyResolvedFile(_path, catalogItemId, "version-id", versionLabel));
    }

    public string? GetDatabaseRoot() => null;
}

internal sealed class InlineAwaitableEvent : IFamilyManagerAwaitableEvent
{
    private static readonly object App = new();

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        actionWithApp(App);
        return Task.CompletedTask;
    }

    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        return Task.FromResult(funcWithApp(App));
    }

    public Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default)
    {
        return asyncActionWithApp(App);
    }

    public void ProcessQueue(object revitApp)
    {
    }

    public void Initialize(Action onRaise)
    {
    }
}

internal sealed class StubClock : IClock
{
    public DateTimeOffset UtcNow => new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Records every marker write; optionally also writes a REAL ES marker onto
/// the embedded family via the real <c>IFamilyVersionStore</c> (used by the
/// marker-first contract test to read the marker back from the document).
/// </summary>
internal sealed class RecordingVersionWriter : IFamilyVersionWriter
{
    private readonly Document? _doc;
    private readonly IFamilyVersionStore? _store;

    public RecordingVersionWriter(Document? doc = null, IFamilyVersionStore? store = null)
    {
        _doc = doc;
        _store = store;
    }

    public List<(string CatalogItemId, string FamilyName, string? VersionLabel)> Calls { get; } = new();

    public Task WriteVersionMarkerAsync(
        string catalogItemId,
        string familyName,
        string? versionLabel,
        int targetRevit,
        CancellationToken ct)
    {
        Calls.Add((catalogItemId, familyName, versionLabel));

        if (_doc is not null && _store is not null)
        {
            var family = new FilteredElementCollector(_doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
            if (family is not null)
            {
                _store.WriteToLoadedFamily(
                    _doc,
                    family.Id,
                    new FamilyVersion(1, catalogItemId, versionLabel ?? string.Empty, DateTimeOffset.UtcNow, targetRevit));
            }
        }

        return Task.CompletedTask;
    }

    public Task WriteSystemTypeMarkerAsync(
        string catalogItemId,
        string typeUniqueId,
        string? versionLabel,
        int targetRevit,
        CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps the real <c>RevitFamilyLoadService</c> and counts nested-reload
/// calls — the contract for "the orchestration borrowed one pre-opened
/// source document" (provider invoked, returned a live document) and for
/// "the second update was a pre-verify skip" (no reload call at all).
/// </summary>
internal sealed class CountingLoadService : IFamilyLoadServiceSourceAware
{
    private readonly RevitFamilyLoadService _inner;

    public CountingLoadService(RevitFamilyLoadService inner)
    {
        _inner = inner;
    }

    public int NestedReloadCalls { get; private set; }
    public bool ProviderInvoked { get; private set; }
    public bool ProviderReturnedDocument { get; private set; }

    public FamilyLoadResult ReloadNestedInFamilyDocument(
        string normalizedPath,
        string familyName,
        bool overwriteParameterValues,
        Func<Document?>? preOpenedSourceDocProvider = null)
    {
        NestedReloadCalls++;
        if (preOpenedSourceDocProvider is not null)
        {
            ProviderInvoked = true;
            if (preOpenedSourceDocProvider() is not null)
            {
                ProviderReturnedDocument = true;
            }
        }
        return _inner.ReloadNestedInFamilyDocument(
            normalizedPath, familyName, overwriteParameterValues, preOpenedSourceDocProvider);
    }

    public Task<FamilyLoadResult> LoadFamilyAsync(
        FamilyResolvedFile file,
        FamilyLoadOptions options,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default)
    {
        return _inner.LoadFamilyAsync(file, options, onStatusMessage, onSharedDecision, nestedSharedNames, ct);
    }

    public Task<FamilyLoadResult> LoadFamilySymbolAsync(
        string filePath,
        string typeName,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        string? catalogItemId = null,
        CancellationToken ct = default)
    {
        return _inner.LoadFamilySymbolAsync(
            filePath, typeName, onStatusMessage, onSharedDecision, nestedSharedNames, catalogItemId, ct);
    }

    public Task<FamilyLoadResult> ReloadFamilyPreservingLoadedTypesAsync(
        FamilyResolvedFile file,
        bool overwriteParameterValues,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default)
    {
        return _inner.ReloadFamilyPreservingLoadedTypesAsync(
            file, overwriteParameterValues, onStatusMessage, onSharedDecision, nestedSharedNames, ct);
    }
}

/// <summary>
/// Corrupts the 1st and 3rd <c>ComputeForLoadable</c> calls
/// (embedded pre-verify and embedded post-verify in the orchestration —
/// FHV10 unified the verification hash into <c>ComputeForLoadable</c>) —
/// the negative contract: a post-verify mismatch fails the update and NO
/// marker is written.
/// </summary>
internal sealed class CorruptEmbeddedVerifyHasher : IFamilyContentHasher
{
    private readonly FamilyContentHasher _inner = new();
    private int _verifyCalls;

    public FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot)
    {
        _verifyCalls++;
        var real = _inner.ComputeForLoadable(snapshot);
        if ((_verifyCalls == 1 || _verifyCalls == 3) && real is not null)
        {
#pragma warning disable CA1845 // string.Concat(AsSpan) does not exist on net48
            return new FamilyContentHash(
                "DEADBEEF" + real.HexString.Substring(8),
                real.FormatVersion,
                real.SourceKind);
#pragma warning restore CA1845
        }
        return real;
    }

    public FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot)
    {
        return _inner.ComputeForSystem(snapshot);
    }
}

internal sealed class NullFamilyManagerDialogService : IFamilyManagerDialogService
{
    public string? ShowOpenFileDialog(string title, string? initialDirectory = null) => null;
    public string? ShowImportDialog(string title, string? initialDirectory = null) => null;
    public string[]? ShowImportFilesDialog(string title, string? initialDirectory = null) => null;
    public string? ShowFolderBrowserDialog(string title, string? initialDirectory = null) => null;
    public void ShowWarning(string title, string message) { }
    public void ShowError(string title, string message) { }
    public void ShowInfo(string title, string message) { }
    public string? ShowInputDialog(string title, string prompt, string defaultText = "", string placeholderText = "") => null;
    public bool ShowConfirmation(string title, string message) => true;
    public DialogResult ShowYesNoCancel(string title, string message) => DialogResult.Yes;
    public bool? ShowCategoryTreeEditor(object viewModel) => null;
    public bool? ShowProjectBaseRulesEditor(object viewModel) => null;
    public bool? ShowParseRuleEditor(object viewModel) => null;
    public bool? ShowFieldLibrary(object viewModel) => null;
    public bool? ShowAllowedValues(object viewModel) => null;
    public string? ShowCategoryPicker(object viewModel) => null;
    public string? ShowOpenJsonDialog(string title, string? initialDirectory = null) => null;
    public string? ShowOpenTextFileDialog(string title, string? initialDirectory = null) => null;
    public bool? ShowSharedParameterPicker(object viewModel) => null;
    public string? ShowSaveJsonDialog(string title, string? defaultFileName = null) => null;
    public bool? ShowProperties(object viewModel) => null;
    public string? ShowAssetOpenFileDialog(string title, FamilyAssetType assetType, string? initialDirectory = null) => null;
    public bool? ShowPresetEditor(object viewModel) => null;
    public bool? ShowAttributeLibrary(object viewModel) => null;
    public bool? ShowProfile(object viewModel) => null;
    public bool? ShowBatchImportDialog(object viewModel) => null;
    public bool? ShowValidationReport(object viewModel) => null;
    public bool? ShowStatusDetails(object viewModel) => null;
    public bool? ShowValidationRulesEditor(object viewModel) => null;
    public void ShowModelessBatchImportDialog(object viewModel) { }
    public void ShowDatabaseUpdateProgressDialog(object viewModel) { }
    public bool? ShowAvatarCropper(object viewModel) => null;
    public SharedFamiliesLoadChoice ShowSharedFamiliesLoadModeDialog(SharedFamilyDecisionRequest request)
        => SharedFamiliesLoadChoice.OverwriteAll;
}

/// <summary>Catalog provider over a fixed item list (stale-check tests).</summary>
internal sealed class StubCatalogProvider : IFamilyCatalogProvider
{
    private readonly IReadOnlyList<FamilyCatalogItem> _items;

    public StubCatalogProvider(IReadOnlyList<FamilyCatalogItem> items)
    {
        _items = items;
    }

    public FamilyCatalogCapabilities GetCapabilities()
        => new(true, true, true, true, true, CatalogProviderKind.Local);
    public Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(FamilyCatalogQuery query, CancellationToken ct = default)
        => Task.FromResult(_items);
    public Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default)
        => Task.FromResult<FamilyCatalogItem?>(_items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal)));
    public Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(string catalogItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FamilyCatalogVersion>>(Array.Empty<FamilyCatalogVersion>());
    public Task<FamilyCatalogVersion?> GetVersionByIdAsync(string catalogItemId, string versionId, CancellationToken ct = default)
        => Task.FromResult<FamilyCatalogVersion?>(null);
    public Task<FamilyCatalogVersion?> GetVersionByLabelAsync(string catalogItemId, string versionLabel, int targetRevitMajorVersion = 0, CancellationToken ct = default)
        => Task.FromResult<FamilyCatalogVersion?>(null);
    public Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default)
        => Task.FromResult<FamilyFileRecord?>(null);
    public Task<int> GetItemCountAsync(CancellationToken ct = default) => Task.FromResult(_items.Count);
    public Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
    public Task<FamilyCatalogItem?> FindByNormalizedNameAsync(string normalizedName, string? familySource = null, CancellationToken ct = default)
        => Task.FromResult<FamilyCatalogItem?>(null);
    public Task<FamilyCatalogItem?> FindByRevitCategoryIdAsync(int revitCategoryId, string familySource, CancellationToken ct = default)
        => Task.FromResult<FamilyCatalogItem?>(null);
    public Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(string hexHash, int hashFormatVersion, string familySource, CancellationToken ct = default)
        => Task.FromResult<ContentHashMatch?>(null);
    public Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default)
        => Task.FromResult(_items);
    public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

internal sealed class NullSystemTypeFinder : ISystemTypeFinder
{
    public ElementId? FindTypeByName(
        Document doc, string typeName, int? categoryOrdinal, string? familyName = null, string? familyKey = null)
        => null;
    public IReadOnlyList<SystemTypeLocation> CollectTypes(
        Document doc, IReadOnlyCollection<int> categoryOrdinals)
        => Array.Empty<SystemTypeLocation>();
}

internal sealed class NullSystemTypeVersionStore : ISystemTypeVersionStore
{
    public FamilyVersion? ReadFromType(Document doc, ElementId typeId) => null;
    public void WriteToType(Document doc, ElementId typeId, FamilyVersion version) { }
    public IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromTypes(Document doc, IEnumerable<ElementId> typeIds)
        => new Dictionary<ElementId, FamilyVersion?>();
}

internal sealed class NullFamilyTypeRepository : IFamilyTypeRepository
{
    public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FamilyTypeDescriptor>>(Array.Empty<FamilyTypeDescriptor>());
    public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FamilyTypeDescriptor>>(Array.Empty<FamilyTypeDescriptor>());
    public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>>(
            new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>());
    public Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
        string catalogItemId, string? versionId, string? fileId, string runId,
        IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    public Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// Decorator over the real extractor that counts
/// <see cref="ExtractFromFamilyDocument"/> calls — the stale-check
/// fast-path contracts assert ZERO extractions (no document opened) and the
/// per-run file-hash cache contract asserts the exact extraction count.
/// </summary>
internal sealed class CountingSnapshotExtractor : IFamilySnapshotExtractor
{
    private readonly IFamilySnapshotExtractor _inner;

    public CountingSnapshotExtractor(IFamilySnapshotExtractor inner)
    {
        _inner = inner;
    }

    public int FamilyDocumentExtractions { get; private set; }

    public FamilySnapshot ExtractFromFamilyDocument(Document familyDoc)
    {
        FamilyDocumentExtractions++;
        return _inner.ExtractFromFamilyDocument(familyDoc);
    }

    public SystemFamilySnapshot ExtractFromProject(
        Document projectDoc, IReadOnlyList<string> typeUniqueIds, BuiltInCategory builtInCategory)
        => _inner.ExtractFromProject(projectDoc, typeUniqueIds, builtInCategory);

    public SystemFamilySnapshot ExtractSystemCategoryFromStagedProject(Document stagedDoc, BuiltInCategory builtInCategory)
        => _inner.ExtractSystemCategoryFromStagedProject(stagedDoc, builtInCategory);

    public SystemTypeSnapshot ExtractSingleSystemType(Document projectDoc, ElementId typeId)
        => _inner.ExtractSingleSystemType(projectDoc, typeId);

    public IReadOnlyList<FamilyGeometryPerType> ExtractGeometryPerType(Document familyDoc, CancellationToken ct = default)
        => _inner.ExtractGeometryPerType(familyDoc, ct);
}
