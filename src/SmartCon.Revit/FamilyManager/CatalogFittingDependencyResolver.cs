using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Catalog-backed implementation of <see cref="IFittingDependencyResolver"/>
/// (Issue #104). Fittings are regular loadable catalog families: reused when
/// already in the project (they live their own stale lifecycle), loaded from
/// the managed storage when missing, reported as unresolved when absent from
/// the catalog too.
/// </summary>
public sealed class CatalogFittingDependencyResolver : IFittingDependencyResolver
{
    private readonly IFamilyCatalogProvider _catalog;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyLoadService _loadService;
    private readonly ISharedNestedFamilyRepository? _nestedSharedRepository;

    public CatalogFittingDependencyResolver(
        IFamilyCatalogProvider catalog,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        ISharedNestedFamilyRepository? nestedSharedRepository = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileResolver);
        ArgumentNullException.ThrowIfNull(loadService);
#else
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (fileResolver is null) throw new ArgumentNullException(nameof(fileResolver));
        if (loadService is null) throw new ArgumentNullException(nameof(loadService));
#endif
        _catalog = catalog;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _nestedSharedRepository = nestedSharedRepository;
    }

    public ElementId? EnsureFitting(
        Document activeDoc,
        string familyName,
        string typeName,
        int targetRevitVersion)
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

        var item = AsyncBridge.RunSync(
            () => _catalog.FindByNormalizedNameAsync(
                FamilySearchNormalizer.Normalize(familyName), "loadable", CancellationToken.None));
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
            result = _loadService.LoadFamilyAsync(
                resolved,
                FamilyLoadOptions.Default with { PreferredName = familyName },
                onStatusMessage: null,
                onSharedDecision: null,
                nestedSharedNames: nestedNames,
                ct: CancellationToken.None).GetAwaiter().GetResult();
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
        return FindSymbol(activeDoc, familyName, typeName)?.Id;
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
