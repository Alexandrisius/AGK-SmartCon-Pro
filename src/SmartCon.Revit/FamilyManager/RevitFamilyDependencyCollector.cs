using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit-side implementation of <see cref="IFamilyDependencyCollector"/>
/// (ADR-066, E1). Resolves routing-rule part tokens to LIVE families in the
/// source document: the rule's "Family:Type" token is matched against the
/// <see cref="FamilySymbol"/> index without string splitting (a colon inside
/// a family or type name cannot break the lookup), and the returned identity
/// is <see cref="Family.UniqueId"/> — never the display name.
/// </summary>
public sealed class RevitFamilyDependencyCollector : IFamilyDependencyCollector
{
    public IReadOnlyList<FamilyDependencyDescriptor> CollectRoutingDependencies(
        Document document,
        SystemFamilySnapshot snapshot)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshot);
#else
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
#endif

        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(CollectRoutingDependencies)),
            ("Category", snapshot.CategoryName));

        var partNames = new List<string>();
        var seenPartNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in snapshot.Types)
        {
            if (type.Routing is null) continue;
            foreach (var rule in type.Routing.Rules)
            {
                // Segments are resolved by the segment synchronizer, never
                // via the fitting catalog (same rule as SystemTypeSyncService).
                if (rule.GroupType == (int)RoutingPreferenceRuleGroupType.Segments) continue;
                if (string.IsNullOrWhiteSpace(rule.PartName)) continue;
                if (seenPartNames.Add(rule.PartName!))
                {
                    partNames.Add(rule.PartName!);
                }
            }
        }

        if (partNames.Count == 0)
        {
            SmartConLogger.Debug("No routing dependency candidates in snapshot");
            return Array.Empty<FamilyDependencyDescriptor>();
        }

        var symbolIndex = BuildSymbolIndex(document);
        var descriptors = new List<FamilyDependencyDescriptor>();
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var partName in partNames)
        {
            if (!symbolIndex.TryGetValue(partName, out var symbol))
            {
                SmartConLogger.Warn(
                    $"Routing rule part '{partName}' not found among project family symbols. " +
                    "[Action: перезагрузите семейство фитинга в проект-источник; правило не даст зависимости в каталоге]");
                continue;
            }

            var family = symbol.Family;
            if (family is null)
            {
                SmartConLogger.Warn(
                    $"Routing rule part '{partName}': symbol has no Family reference. " +
                    "[Action: семейство пропущено — проверьте целостность семейства фитинга в проекте-источнике]");
                continue;
            }

            if (!seenFamilies.Add(family.UniqueId))
            {
                continue;
            }

            if (family.IsInPlace || !family.IsEditable)
            {
                SmartConLogger.Warn(
                    $"Routing dependency '{family.Name}' skipped: IsInPlace={family.IsInPlace}, " +
                    $"IsEditable={family.IsEditable}. " +
                    "[Action: такие семейства нельзя переоткрыть через EditFamily — замените их загружаемым семейством фитинга]");
                continue;
            }

            descriptors.Add(new FamilyDependencyDescriptor(
                Kind: FamilyDependencyKind.Routing,
                PartName: partName,
                FamilyUniqueId: family.UniqueId,
                FamilyName: family.Name,
                CategoryName: family.FamilyCategory?.Name));
        }

        SmartConLogger.Info(
            $"Routing dependencies collected: {descriptors.Count} families from {partNames.Count} rule parts");
        return descriptors;
    }

    /// <summary>
    /// Indexes every FamilySymbol in the document by its routing-rule token
    /// "$"{Family.Name}:{Symbol.Name}"" — the exact format
    /// <c>RevitFamilySnapshotExtractor.ConvertRoutingRule</c> writes into
    /// <c>RoutingRuleSnapshot.PartName</c>. Key collisions are impossible
    /// (family and type names are unique within a project); the indexer
    /// assignment means the LAST symbol wins defensively.
    /// </summary>
    private static Dictionary<string, FamilySymbol> BuildSymbolIndex(Document document)
    {
        var index = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
        var symbols = new FilteredElementCollector(document)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>();
        foreach (var symbol in symbols)
        {
            var familyName = symbol.Family?.Name;
            if (string.IsNullOrEmpty(familyName) || string.IsNullOrEmpty(symbol.Name)) continue;
            var key = $"{familyName}:{symbol.Name}";
            // Indexer assignment (not TryAdd): net48 has no Dictionary.TryAdd
            // and CA1864 forbids ContainsKey+Add on net8. Key collisions are
            // impossible (family and type names are unique within a project);
            // the last symbol wins defensively.
            index[key] = symbol;
        }

        return index;
    }
}
