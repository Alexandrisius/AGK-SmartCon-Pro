using SmartCon.Core.Logging;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Pure-C# resolver that picks the best available name for a shared nested
/// family conflict, given (a) the optional <c>Autodesk.Revit.DB.Family</c>
/// reference that the Revit API supplied (may be <c>null</c> in Revit ≤ 2024.2
/// due to ticket REVIT-198137), and (b) the catalog-DB fallback list that was
/// extracted at import time.
///
/// Extracted from <c>RevitFamilyLoadOptions</c> so the counter + fallback
/// logic is unit-testable in isolation (the Revit API surface uses
/// sealed native types that cannot be mocked).
///
/// Debug-level logging emits a single line per <see cref="Resolve"/> call
/// showing the chosen path. At Debug build level the main
/// <c>smartcon.log</c> shows the full decision chain for every
/// <c>OnSharedFamilyFound</c> callback, which is what you need to verify
/// that REVIT-198137 fallback works in Revit ≤ 2024.2.
/// </summary>
public sealed class SharedFamilyNameResolver
{
    private readonly IReadOnlyList<string> _nestedSharedNames;
    private int _invocationCounter;

    public SharedFamilyNameResolver(IReadOnlyList<string>? nestedSharedNames = null)
    {
        _nestedSharedNames = nestedSharedNames ?? Array.Empty<string>();
        _invocationCounter = 0;
    }

    /// <summary>
    /// Total number of names available in the catalog-DB fallback list.
    /// </summary>
    public int TotalInBatch => _nestedSharedNames.Count;

    /// <summary>
    /// Increments the internal counter and returns a 1-based index of the
    /// current invocation. Safe for repeated calls — the counter is only
    /// reset by constructing a new resolver.
    /// </summary>
    public int NextInvocationIndex() => Interlocked.Increment(ref _invocationCounter);

    /// <summary>
    /// Resolves the name shown in the dialog and the source it came from.
    /// Prefers the Revit API value when present; falls back to the catalog-DB
    /// list indexed by the current invocation counter; finally emits a
    /// generic placeholder when neither source has data.
    /// </summary>
    /// <param name="revitApiName">
    /// The name reported by Revit, or <c>null</c> / whitespace when the
    /// Revit API could not supply one (REVIT-198137).
    /// </param>
    /// <param name="invocationIndex">
    /// 1-based counter value returned by <see cref="NextInvocationIndex"/>.
    /// </param>
    public (string Name, SharedFamilyNameSource Source) Resolve(string? revitApiName, int invocationIndex)
    {
        if (!string.IsNullOrWhiteSpace(revitApiName))
        {
            // Normal path: Revit API supplies the real name.
            // REVIT-198137 is NOT triggered here (ApiName is non-empty).
            SmartConLogger.Debug(
                $"Resolve[#{invocationIndex}]: RevitApi path — name='{revitApiName}' (REVIT-198137 NOT triggered)");
            return (revitApiName!, SharedFamilyNameSource.RevitApi);
        }

        if (_nestedSharedNames.Count > 0
            && invocationIndex - 1 >= 0
            && invocationIndex - 1 < _nestedSharedNames.Count)
        {
            // REVIT-198137 fallback: Revit API returned null/whitespace, so
            // we use the catalog-DB list persisted at import time.
            var fallbackName = _nestedSharedNames[invocationIndex - 1];
            SmartConLogger.Debug(
                $"Resolve[#{invocationIndex}]: CatalogDb fallback — " +
                $"REVIT-198137 TRIGGERED (ApiName was null/empty), using catalog name='{fallbackName}' " +
                $"(index {invocationIndex - 1} of {_nestedSharedNames.Count})");
            return (fallbackName, SharedFamilyNameSource.CatalogDb);
        }

        // Last resort: REVIT-198137 + empty catalog (legacy catalog pre-V13
        // migration OR the family was never re-imported after migration).
        // Dialog shows a generic placeholder so the user can still decide.
        SmartConLogger.Warn(
            $"Resolve[#{invocationIndex}]: FallbackPlaceholder — " +
            $"REVIT-198137 TRIGGERED AND catalog is empty (Count={_nestedSharedNames.Count}, " +
            $"index={invocationIndex - 1}) [Action: re-import the family in Family Manager to populate " +
            $"shared-nested names in the catalog]");
        return ($"<shared nested #{invocationIndex}>", SharedFamilyNameSource.FallbackPlaceholder);
    }
}
