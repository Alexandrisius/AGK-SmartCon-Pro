using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Revit.Extensions;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Session cache of family parameter snapshots (names, formulas, IsInstance)
/// keyed by "docTitle|familyUniqueId". Eliminates repeated EditFamily openings
/// for read-only formula access: the snapshot is built once per family per
/// document (one EditFamily) and reused (phase 3, #161).
/// Invalidated after CTC writes reload the family (LoadFamily).
/// Formulas can only change via family edit + reload — outside PipeConnect
/// sessions in practice; the document qualifier keeps projects isolated.
/// </summary>
public sealed class FamilyFormulaCache
{
    private readonly ConcurrentDictionary<string, FamilyParameterSnapshot?> _cache =
        new(System.StringComparer.Ordinal);

    /// <summary>
    /// Get the snapshot for the instance's family, building it via one
    /// EditFamily on first access. Returns null when EditFamily is forbidden
    /// (doc.IsModifiable) or the family cannot be opened.
    /// </summary>
    public FamilyParameterSnapshot? Get(Document doc, FamilyInstance instance)
    {
        var family = instance.Symbol?.Family;
        if (family is null) return null;

        var key = $"{doc.Title}|{family.UniqueId}";
        if (_cache.TryGetValue(key, out var cached))
        {
            SmartConLogger.Debug($"  FormulaCache HIT '{family.Name}'");
            return cached;
        }

        var snap = EditFamilySession.Run(doc, instance, familyDoc =>
            (FamilyParameterSnapshot?)FamilyParameterSnapshot.Build(familyDoc.FamilyManager));

        // Cache only successful builds: null means EditFamily was unavailable
        // (doc.IsModifiable, or the family is open in the Family Editor) — the
        // next Get must retry, not stick a null for the rest of the session.
        if (snap is not null)
        {
            _cache[key] = snap;
            SmartConLogger.Debug($"  FormulaCache MISS '{family.Name}' → snapshot built ({snap.Parameters.Count} params)");
        }
        else
        {
            SmartConLogger.Debug($"  FormulaCache MISS '{family.Name}' → EditFamily unavailable, NOT cached (will retry)");
        }
        return snap;
    }

    /// <summary>Drop the cached snapshot (call after family edit + reload changes formulas).</summary>
    public void Invalidate(Document doc, Autodesk.Revit.DB.Family family)
    {
        var key = $"{doc.Title}|{family.UniqueId}";
        if (_cache.TryRemove(key, out _))
            SmartConLogger.Debug($"  FormulaCache invalidated '{family.Name}'");
    }
}
