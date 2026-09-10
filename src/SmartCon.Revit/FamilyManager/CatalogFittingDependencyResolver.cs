using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Catalog-backed implementation of <see cref="IFittingDependencyResolver"/>
/// (Issue #104, ADR-066). Fittings are regular loadable catalog families:
/// reused when already in the project (they live their own stale lifecycle),
/// loaded from the managed storage when missing, reported as unresolved when
/// absent from the catalog too. When the caller supplies the parent catalog
/// item id, the catalog lookup goes through <c>family_dependencies</c> links
/// first (exact "Family:Type" match, then same-family match); the name-based
/// lookup remains as the fallback for references staged before E1. A fitting
/// loaded from the catalog receives the ES version marker — it becomes a
/// full participant of the loadable stale lifecycle (ADR-066 §5).
/// </summary>
public sealed class CatalogFittingDependencyResolver : IFittingDependencyResolver
{
    private readonly IFamilyCatalogProvider _catalog;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyLoadService _loadService;
    private readonly IFamilyDependencyRepository _dependencyRepository;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IFamilyVersionStore _versionStore;
    private readonly IClock _clock;
    private readonly ISharedNestedFamilyRepository? _nestedSharedRepository;

    public CatalogFittingDependencyResolver(
        IFamilyCatalogProvider catalog,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        IFamilyDependencyRepository dependencyRepository,
        IFamilyTypeRepository typeRepository,
        IFamilyVersionStore versionStore,
        IClock clock,
        ISharedNestedFamilyRepository? nestedSharedRepository = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileResolver);
        ArgumentNullException.ThrowIfNull(loadService);
        ArgumentNullException.ThrowIfNull(dependencyRepository);
        ArgumentNullException.ThrowIfNull(typeRepository);
        ArgumentNullException.ThrowIfNull(versionStore);
        ArgumentNullException.ThrowIfNull(clock);
#else
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (fileResolver is null) throw new ArgumentNullException(nameof(fileResolver));
        if (loadService is null) throw new ArgumentNullException(nameof(loadService));
        if (dependencyRepository is null) throw new ArgumentNullException(nameof(dependencyRepository));
        if (typeRepository is null) throw new ArgumentNullException(nameof(typeRepository));
        if (versionStore is null) throw new ArgumentNullException(nameof(versionStore));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
#endif
        _catalog = catalog;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _dependencyRepository = dependencyRepository;
        _typeRepository = typeRepository;
        _versionStore = versionStore;
        _clock = clock;
        _nestedSharedRepository = nestedSharedRepository;
    }

    public ElementId? EnsureFitting(
        Document activeDoc,
        string familyName,
        string typeName,
        int targetRevitVersion,
        string? parentCatalogItemId = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(familyName);
        ArgumentNullException.ThrowIfNull(typeName);
#else
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (familyName is null) throw new ArgumentNullException(nameof(familyName));
        if (typeName is null) throw new ArgumentNullException(nameof(typeName));
#endif

        var existing = FindSymbol(activeDoc, familyName, typeName);
        if (existing is not null) return existing.Id;

        using var _scope = SmartConLogger.BeginScope(
            "SystemSync",
            ("Method", nameof(EnsureFitting)),
            ("FamilyName", familyName));

        var item = ResolveFromDependencyLinks(parentCatalogItemId, familyName, typeName);
        if (item is not null)
        {
            SmartConLogger.Debug(
                $"Fitting '{familyName}' resolved via family_dependencies link (CatalogItemId={item.Id})");
        }
        else
        {
            item = AsyncBridge.RunSync(
                () => _catalog.FindByNormalizedNameAsync(
                    FamilySearchNormalizer.Normalize(familyName), "loadable", CancellationToken.None));
        }

        if (item is null)
        {
            SmartConLogger.Warn(
                $"Fitting family '{familyName}' is neither in the project nor in the catalog. " +
                "[Action: import the fitting family into the catalog; the routing rule was skipped]");
            return null;
        }

        var resolved = AsyncBridge.RunSync(
            () => _fileResolver.ResolveForLoadAsync(item.Id, targetRevitVersion, CancellationToken.None));
        if (resolved is null || string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            SmartConLogger.Warn(
                $"Fitting '{familyName}': no catalog file for Revit {targetRevitVersion}. " +
                "[Action: import a version for this Revit into the catalog; the routing rule was skipped]");
            return null;
        }

        // Pre-resolve shared-nested names BEFORE the blocking load: the
        // GetAwaiter().GetResult() below is only safe when every internal
        // await completes synchronously (StaleFamilyUpdater precedent).
        IReadOnlyList<string>? nestedNames = null;
        if (_nestedSharedRepository is not null)
        {
            try
            {
                nestedNames = AsyncBridge.RunSync(
                    () => _nestedSharedRepository.GetNamesForCurrentVersionAsync(
                        item.Id, CancellationToken.None));
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"Fitting '{familyName}': nested names pre-resolve failed — {ex.Message}");
            }
        }

        FamilyLoadResult result;
        try
        {
            // #212 (ADR-023): per-type loading — the routing rule references
            // ONE symbol; pulling every type of the family into the project
            // contradicts the type-centric catalog workflow. Full-family load
            // remains the fallback for typeless families (the synthetic
            // "<default>" type — Revit auto-creates a family-named symbol on
            // load, see #172) and for types renamed in the source project
            // (absent from the catalog version's type list).
            if (ShouldLoadSingleSymbol(resolved, item, typeName))
            {
                SmartConLogger.Debug(
                    $"Fitting '{familyName}': per-type load of symbol '{typeName}' (#212)");
                result = _loadService.LoadFamilySymbolAsync(
                    resolved.AbsolutePath,
                    typeName,
                    onStatusMessage: null,
                    onSharedDecision: null,
                    nestedSharedNames: nestedNames,
                    catalogItemId: item.Id,
                    ct: CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                result = _loadService.LoadFamilyAsync(
                    resolved,
                    FamilyLoadOptions.Default with { PreferredName = familyName },
                    onStatusMessage: null,
                    onSharedDecision: null,
                    nestedSharedNames: nestedNames,
                    ct: CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Fitting '{familyName}': load failed — {ex.Message}. " +
                "[Action: the routing rule was skipped; check the family file in the catalog]");
            return null;
        }

        if (!result.Success)
        {
            SmartConLogger.Warn(
                $"Fitting '{familyName}': load failed — {result.ErrorMessage}. " +
                "[Action: the routing rule was skipped; check the family file in the catalog]");
            return null;
        }

        SmartConLogger.Info($"Fitting '{familyName}' loaded from the catalog for routing preferences.");
        var loadedSymbol = FindSymbol(activeDoc, familyName, typeName)
            // #212 (F1): the per-type path cannot rename the family on load —
            // when the file's internal family name differs from the rule's
            // (renamed file resolved by name-fallback), the catalog item name
            // is the file-name truth to match against.
            ?? (string.Equals(item.Name, familyName, StringComparison.OrdinalIgnoreCase)
                ? null
                : FindSymbol(activeDoc, item.Name, typeName));
        WriteVersionMarker(activeDoc, loadedSymbol, item.Id, resolved.VersionLabel, targetRevitVersion, familyName);
        return loadedSymbol?.Id;
    }

    /// <summary>
    /// ADR-066 §5: resolves the fitting's catalog item through the parent's
    /// <c>family_dependencies</c> links (current version). Match order:
    /// exact "Family:Type" part-name, then same-family (the link stores only
    /// the FIRST part name of a family — several rules may reference
    /// different types of it). Returns <c>null</c> when the parent has no
    /// usable link for this rule (legacy reference, or no parent context).
    /// </summary>
    private FamilyCatalogItem? ResolveFromDependencyLinks(
        string? parentCatalogItemId,
        string familyName,
        string typeName)
    {
        if (string.IsNullOrEmpty(parentCatalogItemId)) return null;

        IReadOnlyList<FamilyDependencyInfo> links;
        try
        {
            links = AsyncBridge.RunSync(
                () => _dependencyRepository.GetForCurrentVersionAsync(
                    parentCatalogItemId!, CancellationToken.None));
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Dependency links read failed for parent {parentCatalogItemId}: {ex.Message} — falling back to name lookup");
            return null;
        }

        if (links.Count == 0) return null;

        var partName = $"{familyName}:{typeName}";
        var match = links.FirstOrDefault(l =>
                l.Kind == FamilyDependencyKind.Routing &&
                string.Equals(l.PartName, partName, StringComparison.Ordinal))
            ?? links.FirstOrDefault(l =>
                l.Kind == FamilyDependencyKind.Routing &&
                string.Equals(FamilyPartOfPartName(l.PartName), familyName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        return AsyncBridge.RunSync(
            () => _catalog.GetItemAsync(match.ChildCatalogItemId, CancellationToken.None));
    }

    /// <summary>
    /// #212: decides whether the resolver may load ONLY the rule's symbol
    /// (<c>LoadFamilySymbolAsync</c>) instead of the whole family. True iff
    /// the catalog version being loaded actually contains a type with the
    /// rule's name. Query failures degrade to the full-family load — the
    /// conservative path that always brings the referenced content.
    /// #216: per-type loading is additionally restricted to PIPE fittings.
    /// The duct-style routing preferences dialog (ducts — and presumably
    /// cable trays / conduits, which share the no-size-criteria dialog)
    /// builds its parts list from a document-level index that Revit
    /// refreshes ONLY on a real full family load (<c>Document.LoadFamily</c>);
    /// <c>LoadFamilySymbol</c> never triggers the refresh, and neither does
    /// a project/dialog reopen or Activate()+Regenerate() (manual tests
    /// 2026-08-06). The pipe dialog computes parts per rule with size
    /// criteria and tolerates per-type loads — it is the only dialog where
    /// the #212 optimization is safe.
    /// </summary>
    private bool ShouldLoadSingleSymbol(
        FamilyResolvedFile resolved,
        FamilyCatalogItem item,
        string typeName)
    {
        if (string.IsNullOrEmpty(resolved.VersionId)) return false;
        if (item.RevitCategoryId != (int)BuiltInCategory.OST_PipeFitting) return false;
        try
        {
            var types = AsyncBridge.RunSync(
                () => _typeRepository.GetTypesForItemVersionAsync(
                    item.Id, resolved.VersionId, CancellationToken.None));
            return types.Any(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Type list query failed (CatalogItemId={item.Id}): {ex.Message} — full family load");
            return false;
        }
    }

    /// <summary>
    /// Extracts the family token from a "Family:Type" part name (text before
    /// the LAST colon — the extractor writes "{Family.Name}:{Symbol.Name}",
    /// and a family name containing ':' would break a first-colon split).
    /// </summary>
    private static string? FamilyPartOfPartName(string? partName)
    {
        if (string.IsNullOrEmpty(partName)) return null;
        var separator = partName!.LastIndexOf(':');
        return separator <= 0 ? null : partName.Substring(0, separator);
    }

    /// <summary>
    /// ADR-066 §5 (Q2): stamps the just-loaded fitting with the ES version
    /// marker so it joins the loadable stale lifecycle like any family loaded
    /// via "Загрузить в проект". Marker failures never fail the resolution —
    /// the fitting stays unmarked and is flagged stale by the next Check.
    /// The store opens its own transaction; the caller (routing sync) runs
    /// OUTSIDE any transaction by contract.
    /// </summary>
    private void WriteVersionMarker(
        Document activeDoc,
        FamilySymbol? loadedSymbol,
        string catalogItemId,
        string? versionLabel,
        int targetRevitVersion,
        string familyName)
    {
        if (loadedSymbol?.Family is null) return;
        try
        {
            _versionStore.WriteToLoadedFamily(activeDoc, loadedSymbol.Family.Id, new FamilyVersion(
                SchemaVersion: FamilyVersion.CurrentSchemaVersion,
                CatalogItemId: catalogItemId,
                VersionLabel: versionLabel ?? string.Empty,
                LoadedAtUtc: _clock.UtcNow,
                SourceRevitVersion: targetRevitVersion));
            SmartConLogger.Debug(
                $"Fitting '{familyName}': version marker written (CatalogItemId={catalogItemId}, label={versionLabel ?? "<none>"})");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Fitting '{familyName}': version marker write failed — {ex.GetType().Name}: {ex.Message}. " +
                "[Action: фитинг останется без маркера — Проверить покажет stale до явной Загрузки в проект]");
        }
    }

    private static FamilySymbol? FindSymbol(Document doc, string familyName, string typeName)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
        foreach (var symbol in collector.Cast<FamilySymbol>())
        {
            if (!string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            var symbolFamilyName = symbol.Family?.Name ?? symbol.FamilyName;
            if (string.Equals(symbolFamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return symbol;
            }
        }
        return null;
    }
}
