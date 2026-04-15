using System.Collections.Generic;

namespace SmartCon.Core.Compatibility;

#if NETFRAMEWORK
public static class NetFrameworkCompat
{
    public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue value)
    {
        if (dict.ContainsKey(key)) return false;
        dict[key] = value;
        return true;
    }

    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
    {
        return dict.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key)
    {
        return dict.TryGetValue(key, out var value) ? value : default;
    }
}
#endif
