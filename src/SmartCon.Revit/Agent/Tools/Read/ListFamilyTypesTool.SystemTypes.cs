using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed partial class ListFamilyTypesTool
{
    /// <summary>
    /// Rows for ids that resolve to a system family type (WallType, PipeType,
    /// …): they have no loadable Family of their own, so familyId stays empty
    /// and familyKind marks them as system. Live case 5 round 11: classify_types
    /// hands out exactly these ids, and rejecting them as "absent" sent the
    /// model chasing an id list_family_types could never return.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildSystemTypeRows(
        Document doc,
        List<ElementType> systemTypes,
        IReadOnlyList<string> parameterNames,
        bool instanceScope,
        HashSet<long>? usedTypeIds,
        CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>(systemTypes.Count);
        var instanceByType = instanceScope
            ? FirstInstanceByType(doc, systemTypes, ct)
            : null;

        foreach (var systemType in systemTypes)
        {
            ct.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object?>
            {
                ["typeId"] = systemType.Id.GetValue(),
                ["name"] = systemType.Name,
                // Revit gives system types an honest family name ("Basic Wall"):
                // report it instead of pretending there is no family at all.
                ["familyName"] = systemType.FamilyName,
                ["category"] = systemType.Category?.Name,
                ["familyKind"] = "system"
            };
            if (usedTypeIds is not null)
                row["isPlaced"] = usedTypeIds.Contains(systemType.Id.GetValue());

            var carrier = instanceByType is not null
                && instanceByType.TryGetValue(systemType.Id.GetValue(), out var instance)
                ? instance
                : systemType;
            if (parameterNames.Count > 0
                && FindParameters(carrier, parameterNames) is { } found)
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in found)
                    values[pair.Key] = DisplayValue(doc, pair.Value);

                if (values.Count > 0)
                    row["values"] = values;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static Dictionary<long, Element>? FirstInstanceByType(
        Document doc,
        List<ElementType> systemTypes,
        CancellationToken ct)
    {
        var wanted = new HashSet<long>(systemTypes.Select(type => type.Id.GetValue()));
        Dictionary<long, Element>? firstByType = null;
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();
            var typeId = element.GetTypeId().GetValue();
            if (!wanted.Contains(typeId))
                continue;

            firstByType ??= new Dictionary<long, Element>();
            if (!firstByType.ContainsKey(typeId))
                firstByType[typeId] = element;
        }

        return firstByType;
    }

    /// <summary>Type ids currently used by placed elements, for isPlaced of system types.</summary>
    private static HashSet<long>? CollectUsedTypeIds(Document doc, bool needPlaced, CancellationToken ct)
    {
        if (!needPlaced)
            return null;

        var used = new HashSet<long>();
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();
            var typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
                used.Add(typeId.GetValue());
        }

        return used;
    }
}
