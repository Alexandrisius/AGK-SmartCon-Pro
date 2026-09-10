using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// FHV8 (#209, ADR-066): computes COMPOSITE content hashes for a closed
/// set of loadable families (a parent plus its whole shared-nested
/// closure). A family's composite hash covers its own content AND the
/// composite hashes of its DIRECT shared-nested children, so a content
/// change deep in the chain (bolt → flange → valve) transitively shifts
/// every ancestor's hash — a re-imported parent then yields a new version
/// instead of a false Duplicate.
/// <para>
/// Inputs are FLAT per-document subtree scans (probe P1: every nesting
/// level is visible in any ancestor's family document). Direct edges are
/// derived by subtraction — <c>Direct(f) = Subtree(f) \ ⋃ Subtree(g)</c>
/// for <c>g ∈ Subtree(f)</c> — no recursive link walking. Edges lost when
/// a child is reachable both directly and through an intermediate are
/// harmless: the child's content reaches the ancestor transitively
/// through the intermediate's hash.
/// </para>
/// <para>
/// Determinism: families are processed in OrdinalIgnoreCase key order,
/// child pairs are sorted by name inside the canonical string, and an
/// unreadable / missing child contributes the constant
/// <see cref="UnreadableChildHash"/> marker (never an exception). Cyclic
/// references cannot exist in real Revit files (LoadFamily rejects a
/// family already present in the target document); a defensive guard
/// breaks a hypothetical cycle with the same marker.
/// </para>
/// </summary>
public sealed class CompositeFamilyHashComposer
{
    /// <summary>
    /// Placeholder hash hex for a child whose content could not be read
    /// (open/extract failure) or that participates in a (hypothetical)
    /// reference cycle. Keeps the parent's hash deterministic; when the
    /// child becomes readable later, the parent's hash legitimately
    /// changes on the next import.
    /// </summary>
    public const string UnreadableChildHash = "UNREADABLE";

    private readonly IFamilyContentHasher _hasher;

    public CompositeFamilyHashComposer(IFamilyContentHasher hasher)
    {
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
    }

    /// <summary>
    /// Composite result of one family: the identity hash AND the
    /// canonical sections — both computed from the SAME enriched snapshot
    /// (own content + direct children's composite hashes), so the section
    /// hashes are always consistent with the identity hash (Issue #249,
    /// Phase 4).
    /// </summary>
    public sealed record CompositeHashResult(
        FamilyContentHash? Hash,
        IReadOnlyList<ContentSectionHash>? Sections);

    /// <summary>
    /// Compose composite hashes for every family in
    /// <paramref name="snapshots"/>. Keys are NORMALIZED family names
    /// (<c>FamilyNameNormalizer.Normalize</c>, case-insensitive);
    /// <paramref name="flatSubtrees"/> maps the same normalized names to
    /// the flat normalized-name lists scanned in each family's own
    /// document. A family absent from <paramref name="flatSubtrees"/> is
    /// treated as having no shared nested children.
    /// </summary>
    /// <returns>
    /// Normalized name → composite hash for every key of
    /// <paramref name="snapshots"/>. The hash is <c>null</c> only when
    /// the underlying hasher returns <c>null</c>.
    /// </returns>
    public IReadOnlyDictionary<string, FamilyContentHash?> Compose(
        IReadOnlyDictionary<string, FamilySnapshot> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> flatSubtrees)
    {
        var detailed = ComposeDetailed(snapshots, flatSubtrees);
        var result = new Dictionary<string, FamilyContentHash?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in detailed)
        {
            result[kvp.Key] = kvp.Value.Hash;
        }
        return result;
    }

    /// <summary>
    /// <see cref="Compose"/> with the canonical sections of the same
    /// enriched snapshots (Issue #249, Phase 4): the NESTEDHASH section
    /// of the returned sections contains the children's composite hashes,
    /// exactly like the identity hash — sections computed from the raw
    /// snapshots would diverge from the composite identity.
    /// </summary>
    public IReadOnlyDictionary<string, CompositeHashResult> ComposeDetailed(
        IReadOnlyDictionary<string, FamilySnapshot> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> flatSubtrees)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(flatSubtrees);
#else
        if (snapshots is null) throw new ArgumentNullException(nameof(snapshots));
        if (flatSubtrees is null) throw new ArgumentNullException(nameof(flatSubtrees));
#endif

        var orderedNames = snapshots.Keys
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directEdges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in orderedNames)
        {
            directEdges[name] = DeriveDirectChildren(name, snapshots, flatSubtrees);
        }

        var memo = new Dictionary<string, CompositeHashResult>(StringComparer.OrdinalIgnoreCase);
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in orderedNames)
        {
            ComputeRecursive(name, snapshots, directEdges, memo, inProgress);
        }

        var withChildren = directEdges.Count(d => d.Value.Count > 0);
        if (withChildren > 0)
        {
            SmartConLogger.Info(
                $"Composite hashes composed: {snapshots.Count} families, {withChildren} with shared-nested children");
        }

        return memo;
    }

    /// <summary>
    /// Direct children of <paramref name="name"/>: its flat subtree minus
    /// the subtrees of every other member of that subtree. A descendant
    /// reachable through an intermediate is dropped from the direct set —
    /// its content still flows in through the intermediate's hash.
    /// </summary>
    private static IReadOnlyList<string> DeriveDirectChildren(
        string name,
        IReadOnlyDictionary<string, FamilySnapshot> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> flatSubtrees)
    {
        if (!flatSubtrees.TryGetValue(name, out var subtree) || subtree.Count == 0)
        {
            return Array.Empty<string>();
        }

        var indirect = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in subtree)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(member, name))
            {
                continue;
            }

            if (flatSubtrees.TryGetValue(member, out var memberSubtree))
            {
                indirect.UnionWith(memberSubtree);
            }
        }

        return subtree
            .Where(child => !indirect.Contains(child))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CompositeHashResult ComputeRecursive(
        string name,
        IReadOnlyDictionary<string, FamilySnapshot> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> directEdges,
        Dictionary<string, CompositeHashResult> memo,
        HashSet<string> inProgress)
    {
        if (memo.TryGetValue(name, out var existing))
        {
            return existing;
        }

        if (!inProgress.Add(name))
        {
            // Defensive only — real Revit files cannot contain reference
            // cycles (LoadFamily rejects them). Break deterministically.
            SmartConLogger.Warn(
                $"Composite hash: cyclic shared-nested reference involving '{name}' — " +
                $"child edge substituted with the {UnreadableChildHash} marker " +
                "[Action: проверьте семейство в Revit — циклическая вложенность недопустима]");
            return new CompositeHashResult(null, null);
        }

        var snapshot = snapshots[name];
        var children = new List<NestedContentHash>();
        foreach (var child in directEdges[name])
        {
            string childHash;
            if (!snapshots.ContainsKey(child))
            {
                childHash = UnreadableChildHash;
                SmartConLogger.Warn(
                    $"Composite hash of '{name}': shared nested '{child}' has no extracted snapshot — " +
                    $"using the {UnreadableChildHash} marker " +
                    "[Action: семейство будет импортировано; при расхождении дедупликации переимпортируйте родителя после исправления вложенного]");
            }
            else
            {
                childHash = ComputeRecursive(child, snapshots, directEdges, memo, inProgress).Hash?.HexString
                    ?? UnreadableChildHash;
            }

            children.Add(new NestedContentHash(child, childHash));
        }

        // Identity hash AND sections from the SAME enriched snapshot —
        // the NESTEDHASH section stays consistent with the identity hash.
        var enriched = snapshot with { SharedNestedContentHashes = children };
        var composite = _hasher.ComputeForLoadable(enriched);
        var sections = _hasher.ComputeSectionsForLoadable(enriched);
        var result = new CompositeHashResult(composite, sections);
        memo[name] = result;
        inProgress.Remove(name);
        return result;
    }
}
