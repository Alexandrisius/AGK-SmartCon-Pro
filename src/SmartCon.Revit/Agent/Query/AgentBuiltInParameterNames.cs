using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Query;

internal static class AgentBuiltInParameterNames
{
    private static readonly Lazy<Dictionary<long, string>> Names =
        new(BuildMap, isThreadSafe: true);

    internal static string GetName(ElementId id)
    {
        var value = id.GetValue();
        return Names.Value.TryGetValue(value, out var name)
            ? name
            : "#" + value;
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