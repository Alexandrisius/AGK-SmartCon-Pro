using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Query;

internal static class AgentBuiltInParameterNames
{
    private static readonly Lazy<Dictionary<long, string>> Names =
        new(BuildMap, isThreadSafe: true);

    private static readonly Lazy<Dictionary<string, long>> Ids =
        new(BuildReverseMap, isThreadSafe: true);

    internal static string GetName(ElementId id)
    {
        var value = id.GetValue();
        return Names.Value.TryGetValue(value, out var name)
            ? name
            : "#" + value;
    }

    internal static bool TryGetId(string name, out long id)
    {
        if (Ids.Value.TryGetValue(name, out id))
            return true;

        id = 0;
        return false;
    }

    private static Dictionary<string, long> BuildReverseMap()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Names.Value)
        {
            if (!map.ContainsKey(pair.Value))
                map[pair.Value] = pair.Key;
        }

        return map;
    }

    private static Dictionary<long, string> BuildMap()
    {
        var map = new Dictionary<long, string>();
        foreach (BuiltInParameter value in Enum.GetValues(typeof(BuiltInParameter)))
        {
            try
            {
                var id = new ElementId(value).GetValue();
                if (!map.ContainsKey(id))
                    map[id] = value.ToString();
            }
            catch (Exception)
            {
            }
        }

        return map;
    }
}