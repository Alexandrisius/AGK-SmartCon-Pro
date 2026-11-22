using System.Globalization;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Text preparation of classify_types: groups the selected type snapshots into
/// families and composes the matching texts - the family text (stage 1) and the
/// per-type queries (stage 2, the type name plus the values that differ between
/// the family's types).
/// </summary>
internal static class AgentClassificationTexts
{
    private const int MaxFamilyTextLength = 600;

    internal static IReadOnlyList<ClassifyFamilyGroup> GroupTypes(
        IReadOnlyList<ClassifyTypeSnapshot> types)
    {
        var groups = new List<ClassifyFamilyGroup>();
        var byKey = new Dictionary<string, (ClassifyFamilyGroup Group, List<string> Values, HashSet<string> Common)>(
            StringComparer.Ordinal);
        foreach (var type in types)
        {
            var key = type.FamilyId is { } familyId
                ? "f" + familyId.ToString(CultureInfo.InvariantCulture)
                : "sys:" + type.Category + "|" + type.FamilyName;
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (
                    new ClassifyFamilyGroup
                    {
                        Key = key,
                        Category = type.Category,
                        FamilyName = type.FamilyName,
                        FamilyId = type.FamilyId
                    },
                    new List<string>(),
                    new HashSet<string>(type.Values, StringComparer.OrdinalIgnoreCase));
                byKey[key] = entry;
                groups.Add(entry.Group);
            }
            else
            {
                entry.Common.IntersectWith(new HashSet<string>(
                    type.Values, StringComparer.OrdinalIgnoreCase));
            }

            entry.Group.Types = entry.Group.Types.Append(type).ToArray();
            foreach (var value in type.Values)
            {
                if (!entry.Values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    entry.Values.Add(value);
            }
        }

        foreach (var entry in byKey.Values)
        {
            var group = entry.Group;
            var familyParts = new List<string> { group.Category, group.FamilyName };
            familyParts.AddRange(entry.Values);
            var familyText = JoinParts(familyParts);
            group.FamilyText = familyText.Length > MaxFamilyTextLength
                ? familyText.Substring(0, MaxFamilyTextLength)
                : familyText;
            group.FamilyNameTokens = AgentSemanticLexicon.Tokenize(group.FamilyName);
            group.TypeQueries = group.Types.Select(type =>
                {
                    var parts = new List<string>();
                    if (group.Types.Count == 1)
                    {
                        parts.Add(type.Category);
                        parts.Add(type.FamilyName);
                        parts.Add(type.TypeName);
                        parts.AddRange(type.Values);
                    }
                    else
                    {
                        parts.Add(type.FamilyName);
                        parts.Add(type.TypeName);
                        parts.AddRange(type.Values.Where(value => !entry.Common.Contains(value)));
                    }

                    return JoinParts(parts);
                })
                .ToArray();
        }

        return groups;
    }

    private static string JoinParts(IReadOnlyList<string> parts)
    {
        var flat = new List<string>();
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part) && !flat.Contains(part))
                flat.Add(part);
        }

        return string.Join("; ", flat);
    }
}
