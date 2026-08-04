using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="ISystemTypeSyncOrchestrator"/>
/// (Issue #104). Owns the mini-project lifetime: resolves the managed file,
/// opens it once per batch, closes it in <c>finally</c>. All methods run on
/// the Revit main thread (I-01).
/// </summary>
public sealed class SystemFamilySyncOrchestrator : ISystemTypeSyncOrchestrator
{
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly ISystemTypeSyncService _syncService;
    private readonly ISystemTypeFinder _typeFinder;
    private readonly ISystemTypeVersionStore _versionStore;

    public SystemFamilySyncOrchestrator(
        IFamilyFileResolver fileResolver,
        IFamilyCatalogProvider catalog,
        ISystemTypeSyncService syncService,
        ISystemTypeFinder typeFinder,
        ISystemTypeVersionStore versionStore)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(fileResolver);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(syncService);
        ArgumentNullException.ThrowIfNull(typeFinder);
        ArgumentNullException.ThrowIfNull(versionStore);
#else
        if (fileResolver is null) throw new ArgumentNullException(nameof(fileResolver));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (syncService is null) throw new ArgumentNullException(nameof(syncService));
        if (typeFinder is null) throw new ArgumentNullException(nameof(typeFinder));
        if (versionStore is null) throw new ArgumentNullException(nameof(versionStore));
#endif
        _fileResolver = fileResolver;
        _catalog = catalog;
        _syncService = syncService;
        _typeFinder = typeFinder;
        _versionStore = versionStore;
    }

    public bool IsProjectTypeCurrent(
        Document activeDoc,
        string catalogItemId,
        string typeName,
        int targetRevitVersion,
        string? familyName = null,
        string? familyKey = null)
    {
        if (activeDoc is null) return false;

        var resolved = AsyncBridge.RunSync(
            () => _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevitVersion));
        if (resolved is null || string.IsNullOrEmpty(resolved.AbsolutePath))
            return false;

        var categoryOrdinal = ResolveCategoryOrdinal(catalogItemId);
        // #183: the marker check must find the type of the SAME family —
        // otherwise an up-to-date "Conduit with Fittings: Стандарт" would
        // satisfy the fast path for "Conduit without Fittings: Стандарт".
        // #190: the locale-invariant key is the primary filter.
        var typeId = _typeFinder.FindTypeByName(activeDoc, typeName, categoryOrdinal, familyName, familyKey);
        if (typeId is null) return false;

        var marker = _versionStore.ReadFromType(activeDoc, typeId);
        if (marker is null) return false;

        // Same verdict logic as the stale detector (SystemTypeStaleLogic):
        // placement re-syncs exactly when a Check would mark the type stale.
        return SystemTypeStaleLogic.ComputeReason(
            marker, catalogItemId, resolved.VersionLabel, targetRevitVersion) == StaleReason.None;
    }

    public SystemFamilySyncResult SyncTypes(
        Document activeDoc,
        string catalogItemId,
        IReadOnlyList<SystemTypeRef> types,
        int targetRevitVersion)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(catalogItemId);
        ArgumentNullException.ThrowIfNull(types);
#else
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (catalogItemId is null) throw new ArgumentNullException(nameof(catalogItemId));
        if (types is null) throw new ArgumentNullException(nameof(types));
#endif

        using var _scope = SmartConLogger.BeginScope(
            "SystemSync",
            ("Method", nameof(SyncTypes)),
            ("CatalogItemId", catalogItemId),
            ("Count", types.Count));

        if (types.Count == 0)
        {
            return new SystemFamilySyncResult(catalogItemId, Array.Empty<SystemTypeSyncResult>());
        }

        var resolved = AsyncBridge.RunSync(
            () => _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevitVersion));
        if (resolved is null || string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            SmartConLogger.Warn(
                $"SyncTypes[{catalogItemId}]: no managed file resolved for Revit {targetRevitVersion}. " +
                "[Action: import a version for this Revit into the catalog; types were not synchronized]");
            return FailAll(catalogItemId, types, "No managed file resolved for the current Revit version");
        }

        Document? sourceDoc = null;
        try
        {
            sourceDoc = activeDoc.Application.OpenDocumentFile(resolved.AbsolutePath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"SyncTypes[{catalogItemId}]: OpenDocumentFile failed: {ex.Message}");
            return FailAll(catalogItemId, types, $"OpenDocumentFile failed: {ex.Message}");
        }

        try
        {
            // Manual test 2026-08-04 (round 3): resolve the category ONCE per
            // batch and scope every reference lookup with it — an unscoped
            // name match can bind an unrelated ElementType (electrical
            // settings objects like DistributionSysType share the name and
            // the degenerate 'Single' family key with the real WireType).
            var categoryOrdinal = ResolveCategoryOrdinal(catalogItemId);
            var results = new List<SystemTypeSyncResult>(types.Count);
            foreach (var type in types)
            {
                try
                {
                    results.Add(_syncService.SyncTypeFromSource(
                        sourceDoc,
                        activeDoc,
                        type.Name,
                        catalogItemId,
                        resolved.VersionLabel ?? string.Empty,
                        targetRevitVersion,
                        type.FamilyName,
                        type.FamilyKey,
                        categoryOrdinal));
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"SyncTypes[{catalogItemId}]: type '{type.Name}' failed: {ex.Message}. " +
                        "[Action: type skipped, batch continues]");
                    results.Add(new SystemTypeSyncResult(
                        type.Name, SystemTypeSyncStatus.Failed, 0, 0, ex.Message));
                }
            }

            var succeeded = results.Count(r => r.IsSuccess);
            SmartConLogger.Info(
                $"SyncTypes[{catalogItemId}]: {succeeded}/{results.Count} types synchronized.");
            return new SystemFamilySyncResult(catalogItemId, results);
        }
        finally
        {
            CloseAndRelease(sourceDoc);
        }
    }

    private int? ResolveCategoryOrdinal(string catalogItemId)
    {
        try
        {
            var item = AsyncBridge.RunSync(
                () => _catalog.GetItemAsync(catalogItemId, CancellationToken.None));
            return item?.RevitCategoryId;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"ResolveCategoryOrdinal[{catalogItemId}]: {ex.Message} — category filter skipped.");
            return null;
        }
    }

    private static SystemFamilySyncResult FailAll(
        string catalogItemId, IReadOnlyList<SystemTypeRef> types, string error)
    {
        var results = types
            .Select(t => new SystemTypeSyncResult(t.Name, SystemTypeSyncStatus.Failed, 0, 0, error))
            .ToList();
        return new SystemFamilySyncResult(catalogItemId, results);
    }

    private static void CloseAndRelease(Document doc)
    {
        try { doc.Close(false); } catch { }
        // REVIT-237190: best-effort synchronous COM cleanup after Close —
        // throws ArgumentException on Revit versions where Document is a
        // managed wrapper, which is fine to ignore.
        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(doc); } catch { }
    }
}
