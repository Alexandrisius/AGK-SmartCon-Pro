using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// Default implementation of <see cref="IFamilyVersionWriter"/>. Marshals the
/// Revit-API calls (<c>FindByName</c>, <c>WriteToLoadedFamily</c>) onto the
/// Revit main thread via <see cref="IFamilyManagerAwaitableEvent"/> (I-01).
/// </summary>
internal sealed class FamilyVersionWriter : IFamilyVersionWriter
{
    private readonly IFamilyVersionStore _versionStore;
    private readonly IFamilyManagerAwaitableEvent _awaitable;
    private readonly IRevitContext _revitContext;
    private readonly IFamilyFinder _familyFinder;
    private readonly IClock _clock;

    public FamilyVersionWriter(
        IFamilyVersionStore versionStore,
        IFamilyManagerAwaitableEvent awaitable,
        IRevitContext revitContext,
        IFamilyFinder familyFinder,
        IClock clock)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(versionStore);
        ArgumentNullException.ThrowIfNull(awaitable);
        ArgumentNullException.ThrowIfNull(revitContext);
        ArgumentNullException.ThrowIfNull(familyFinder);
        ArgumentNullException.ThrowIfNull(clock);
#else
        if (versionStore is null) throw new ArgumentNullException(nameof(versionStore));
        if (awaitable is null) throw new ArgumentNullException(nameof(awaitable));
        if (revitContext is null) throw new ArgumentNullException(nameof(revitContext));
        if (familyFinder is null) throw new ArgumentNullException(nameof(familyFinder));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
#endif
        _versionStore = versionStore;
        _awaitable = awaitable;
        _revitContext = revitContext;
        _familyFinder = familyFinder;
        _clock = clock;
    }

    public Task WriteVersionMarkerAsync(
        string catalogItemId,
        string familyName,
        string? versionLabel,
        int targetRevit,
        CancellationToken ct)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalogItemId);
        ArgumentNullException.ThrowIfNull(familyName);
#else
        if (catalogItemId is null) throw new ArgumentNullException(nameof(catalogItemId));
        if (familyName is null) throw new ArgumentNullException(nameof(familyName));
#endif

        var version = new FamilyVersion(
            SchemaVersion: FamilyVersion.CurrentSchemaVersion,
            CatalogItemId: catalogItemId,
            VersionLabel: versionLabel ?? string.Empty,
            LoadedAtUtc: _clock.UtcNow,
            SourceRevitVersion: targetRevit);

        // Note: versionLabel may legitimately be null/empty for legacy families;
        // FamilyVersion.VersionLabel is non-nullable, so we coerce at the boundary.

        return _awaitable.RaiseAsyncTask(_ =>
        {
            var doc = _revitContext.GetDocument();
            var familyId = _familyFinder.FindByName(doc, familyName);
            if (familyId is null)
            {
                SmartConLogger.Info(
                    $"WriteVersionMarker: family '{familyName}' not loaded in document. " +
                    "[Action: marker skipped, will be re-applied on next load]");
                return Task.CompletedTask;
            }
            _versionStore.WriteToLoadedFamily(doc, familyId, version);
            return Task.CompletedTask;
        }, ct);
    }
}
