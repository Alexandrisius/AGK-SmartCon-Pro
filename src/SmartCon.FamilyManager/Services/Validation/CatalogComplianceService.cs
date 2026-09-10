using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Validation;

/// <summary>
/// Catalog compliance check (#259): catalog items vs the effective
/// validation rules of their category. Pure SQLite + pure engine via
/// <see cref="IFamilyImportValidationService.ValidateCatalogItemAsync"/> —
/// no Revit API, no open document. Item enumeration mirrors
/// <c>StaleDetector.CheckCategoryAsync</c> (including the synthetic
/// "__no_category__" id); effective rules are cached per category for the
/// whole run (the rule resolver hits SQLite on every call).
/// <para>
/// Session snapshot (modelled after <see cref="FamilyStaleSnapshot"/>):
/// in-memory verdicts under a lock, invalidated on rule edits (subscribed to
/// <see cref="IFamilyManagerMetadataMediator.MetadataChanged"/> in the ctor),
/// database switch / actualization (explicit <see cref="InvalidateCache"/>
/// calls from the main VM) and re-import (<see cref="InvalidateItems"/>).
/// </para>
/// </summary>
internal sealed class CatalogComplianceService : ICatalogComplianceService
{
    /// <summary>Synthetic id of the «Без категории» tree node (same contract
    /// as <c>StaleDetector</c> / <c>FamilyManagerMainViewModel.Tree</c>).</summary>
    private const string NoCategoryId = "__no_category__";

    private readonly IFamilyCatalogProvider _catalog;
    private readonly IFamilyImportValidationService _validationService;
    private readonly IClock _clock;
    private readonly object _cacheLock = new();
    private CatalogComplianceSnapshot? _cachedSnapshot;

    public CatalogComplianceService(
        IFamilyCatalogProvider catalog,
        IFamilyImportValidationService validationService,
        IFamilyManagerMetadataMediator metadataMediator,
        IClock clock)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // Rule edits (rules editor save, binding CRUD, JSON import) change the
        // effective rules — every stored verdict becomes a lie. Both sides are
        // DI singletons with the same lifetime, so no unsubscription needed.
        if (metadataMediator is null) throw new ArgumentNullException(nameof(metadataMediator));
        metadataMediator.MetadataChanged += OnMetadataChanged;
    }

    private void OnMetadataChanged()
    {
        SmartConLogger.Info(
            "CatalogCompliance: metadata changed (rules/bindings edited) — session compliance snapshot invalidated. " +
            "[Action: повторите «Проверить → Правила» для свежих вердиктов]");
        InvalidateCache();
    }

    public async Task<IReadOnlyList<ComplianceCheckResult>> CheckCategoriesAsync(
        IReadOnlyList<string> categoryIds,
        IProgress<ComplianceCheckProgress>? progress = null,
        CancellationToken ct = default)
    {
        var scopeIds = string.Join(",", categoryIds);
        using var _scope = SmartConLogger.BeginScope(
            "CatalogCompliance",
            ("Method", nameof(CheckCategoriesAsync)),
            ("CategoryIds", scopeIds));

        var items = await QueryItemsAsync(categoryIds, ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            SmartConLogger.Info("CheckCategories: no catalog items in scope");
            return [];
        }

        // Rules hit SQLite on every resolve — cache per category for the run
        // (batch import does the same: one resolve per category group).
        var rulesCache = new Dictionary<string, IReadOnlyList<EffectiveValidationRule>>(StringComparer.Ordinal);
        var results = new List<ComplianceCheckResult>(items.Count);
        var done = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new ComplianceCheckProgress(done, items.Count, item.Name));
            results.Add(await CheckSingleAsync(item, rulesCache, ct).ConfigureAwait(false));
        }

        MergeInto(results);

        var failCount = results.Count(r => r.Status == ComplianceStatus.Fail);
        var cannotVerifyCount = results.Count(r => r.Status == ComplianceStatus.CannotVerify);
        SmartConLogger.Info(
            $"CheckCategories completed: {results.Count} item(s), fail={failCount}, cannotVerify={cannotVerifyCount}, " +
            $"categories={categoryIds.Count}");
        return results;
    }

    public async Task<ComplianceCheckResult> CheckItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope(
            "CatalogCompliance",
            ("Method", nameof(CheckItemAsync)),
            ("CatalogItemId", catalogItemId));

        var item = await _catalog.GetItemAsync(catalogItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            SmartConLogger.Warn(
                $"CheckItem: catalog item '{catalogItemId}' not found — verdict CannotVerify. " +
                "[Action: обновите дерево каталога (Refresh) — элемент мог быть удалён]");
            var missing = ComplianceCheckResult.CannotVerify(catalogItemId, null);
            MergeInto([missing]);
            return missing;
        }

        var result = await CheckSingleAsync(
            item, new Dictionary<string, IReadOnlyList<EffectiveValidationRule>>(StringComparer.Ordinal), ct)
            .ConfigureAwait(false);
        MergeInto([result]);
        SmartConLogger.Info(
            $"CheckItem completed: '{item.Name}' → {result.Status} (violations={result.Violations.Count}, rules={result.RuleCount})");
        return result;
    }

    /// <summary>
    /// One item: effective rules of its category (from the run cache) →
    /// <c>ValidateCatalogItemAsync</c>. Per-item error isolation: a corrupt
    /// row must not sink the whole category run — it degrades to
    /// <see cref="ComplianceStatus.CannotVerify"/> (the notice points at
    /// «Обновить базу», which repairs extraction data).
    /// </summary>
    private async Task<ComplianceCheckResult> CheckSingleAsync(
        FamilyCatalogItem item,
        Dictionary<string, IReadOnlyList<EffectiveValidationRule>> rulesCache,
        CancellationToken ct)
    {
        var cacheKey = item.CategoryId ?? string.Empty;
        if (!rulesCache.TryGetValue(cacheKey, out var rules))
        {
            rules = await _validationService.GetEffectiveRulesAsync(item.CategoryId, ct).ConfigureAwait(false);
            rulesCache[cacheKey] = rules;
        }

        // No rules = free pass (same semantics as the import gate) — skip the
        // extraction reads entirely.
        if (rules.Count == 0)
        {
            return ComplianceCheckResult.Pass(item.Id, item.CategoryId, 0);
        }

        FamilyValidationReport? report;
        try
        {
            report = await _validationService.ValidateCatalogItemAsync(item.Id, rules, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"CheckSingle: validation of '{item.Name}' ({item.Id}) failed: {ex.GetType().Name}: {ex.Message} — verdict CannotVerify. " +
                "[Action: выполните «Обновить базу», затем повторите «Проверить → Правила»]");
            return ComplianceCheckResult.CannotVerify(item.Id, item.CategoryId);
        }

        if (report is null)
        {
            return ComplianceCheckResult.CannotVerify(item.Id, item.CategoryId);
        }

        return report.IsValid
            ? ComplianceCheckResult.Pass(item.Id, item.CategoryId, rules.Count)
            : ComplianceCheckResult.Fail(item.Id, item.CategoryId, report.Violations, report.RulesEvaluated, rules.Count);
    }

    /// <summary>
    /// Item enumeration for the category scope. Mirrors
    /// <c>StaleDetector.CheckCategoryAsync</c>: "__no_category__" selects
    /// NULL/empty category rows via <c>IncludeUncategorized</c>, real ids go
    /// through <c>CategoryIdsFilter</c> (the SQL builder ORs the two).
    /// </summary>
    private async Task<IReadOnlyList<FamilyCatalogItem>> QueryItemsAsync(
        IReadOnlyList<string> categoryIds, CancellationToken ct)
    {
        var hasUncategorized = categoryIds.Count > 0 && categoryIds.Any(id => id == NoCategoryId);
        var realCategoryIds = categoryIds.Where(id => id != NoCategoryId).ToList();
        var hasRealFilter = realCategoryIds.Count > 0;

        var query = new FamilyCatalogQuery(
            SearchText: null,
            CategoryFilter: null,
            StatusFilter: null,
            Tags: null,
            Sort: FamilyCatalogSort.NameAsc,
            Offset: 0,
            Limit: int.MaxValue,
            IncludeUncategorized: hasUncategorized,
            CategoryIdsFilter: hasRealFilter ? realCategoryIds : null);
        return await _catalog.SearchAsync(query, ct).ConfigureAwait(false);
    }

    /// <summary>Mutable copy of the cached verdicts. Manual loop, NOT the
    /// <c>Dictionary(IReadOnlyDictionary, comparer)</c> ctor — that overload
    /// is .NET 8+ only and breaks the net48 build (R21-R24).</summary>
    private Dictionary<string, ComplianceCheckResult> CopyResults()
    {
        var dict = new Dictionary<string, ComplianceCheckResult>(StringComparer.Ordinal);
        if (_cachedSnapshot is not null)
        {
            foreach (var kvp in _cachedSnapshot.Results)
            {
                dict[kvp.Key] = kvp.Value;
            }
        }
        return dict;
    }

    private void MergeInto(IReadOnlyList<ComplianceCheckResult> newResults)
    {
        if (newResults.Count == 0) return;
        lock (_cacheLock)
        {
            var dict = CopyResults();
            foreach (var result in newResults)
            {
                dict[result.CatalogItemId] = result;
            }
            _cachedSnapshot = new CatalogComplianceSnapshot(dict, _clock.UtcNow);
        }
    }

    public CatalogComplianceSnapshot? GetCachedSnapshot()
    {
        lock (_cacheLock) return _cachedSnapshot;
    }

    public CatalogComplianceSnapshot GetMergedSnapshot(IReadOnlyList<ComplianceCheckResult> newResults)
    {
        lock (_cacheLock)
        {
            var dict = CopyResults();
            foreach (var result in newResults)
            {
                dict[result.CatalogItemId] = result;
            }
            return new CatalogComplianceSnapshot(dict, _clock.UtcNow);
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            if (_cachedSnapshot is null) return;
            var count = _cachedSnapshot.Results.Count;
            _cachedSnapshot = null;
            SmartConLogger.Debug($"CatalogCompliance: snapshot invalidated ({count} verdict(s) dropped)");
        }
    }

    public void InvalidateItems(IReadOnlyCollection<string> catalogItemIds)
    {
        if (catalogItemIds is null || catalogItemIds.Count == 0) return;
        lock (_cacheLock)
        {
            if (_cachedSnapshot is null) return;
            var dict = CopyResults();
            var removed = 0;
            foreach (var id in catalogItemIds)
            {
                if (dict.Remove(id)) removed++;
            }
            if (removed == 0) return;
            _cachedSnapshot = new CatalogComplianceSnapshot(dict, _clock.UtcNow);
            SmartConLogger.Debug(
                $"CatalogCompliance: {removed} verdict(s) dropped for re-imported item(s); snapshot size {_cachedSnapshot.Results.Count}");
        }
    }
}
