using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure C# parser for Revit shared parameter files (ФОП, .txt).
/// File layout: tab-delimited lines, "#" comments, "*GROUP ID NAME" and
/// "*PARAM GUID NAME DATATYPE DATACATEGORY GROUP VISIBLE DESCRIPTION
/// USERMODIFIABLE [HIDEWHENNOVALUE]" header lines followed by GROUP/PARAM
/// data lines. Column order is taken from the header lines with a fixed
/// fallback so files without headers still parse.
/// </summary>
public sealed class SharedParameterFileParser : ISharedParameterFileParser
{
    public IReadOnlyList<SharedParameterEntry> ParseFile(string filePath)
    {
        // StreamReader detects UTF-8/UTF-16LE/UTF-16BE via BOM and falls back
        // to UTF-8 — this matches how Revit writes shared parameter files.
        var content = File.ReadAllText(filePath);
        return ParseContent(content);
    }

    public IReadOnlyList<SharedParameterEntry> ParseContent(string content)
    {
        var groups = new Dictionary<int, string>();
        var entries = new List<SharedParameterEntry>();
        string[]? groupHeader = null;
        string[]? paramHeader = null;
        var sawParamSection = false;

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split('\t');
            if (parts.Length == 0)
                continue;

            switch (parts[0])
            {
                case "*GROUP":
                    groupHeader = parts;
                    break;
                case "*PARAM":
                    paramHeader = parts;
                    sawParamSection = true;
                    break;
                case "GROUP":
                    ParseGroupLine(parts, groupHeader, groups);
                    break;
                case "PARAM":
                    sawParamSection = true;
                    var entry = ParseParamLine(parts, paramHeader, groups);
                    if (entry is not null)
                        entries.Add(entry);
                    break;
            }
        }

        if (!sawParamSection)
            throw new InvalidDataException("Not a Revit shared parameter file: no *PARAM section found.");

        return entries;
    }

    private static void ParseGroupLine(string[] parts, string[]? header, Dictionary<int, string> groups)
    {
        var idText = Get(parts, Col(header, "ID", 1));
        var name = Get(parts, Col(header, "NAME", 2));
        if (idText is null || name is null)
            return;
        if (int.TryParse(idText, out var id))
            groups[id] = name;
    }

    private static SharedParameterEntry? ParseParamLine(
        string[] parts, string[]? header, IReadOnlyDictionary<int, string> groups)
    {
        var guidText = Get(parts, Col(header, "GUID", 1));
        var name = Get(parts, Col(header, "NAME", 2));
        if (string.IsNullOrWhiteSpace(name) || !Guid.TryParse(guidText, out var guid))
            return null;

        var dataType = Get(parts, Col(header, "DATATYPE", 3)) ?? string.Empty;
        var dataCategory = Get(parts, Col(header, "DATACATEGORY", 4));
        var groupIdText = Get(parts, Col(header, "GROUP", 5));
        var description = Get(parts, Col(header, "DESCRIPTION", 7));

        string? groupName = null;
        if (groupIdText is not null && int.TryParse(groupIdText, out var groupId))
            groups.TryGetValue(groupId, out groupName);

        return new SharedParameterEntry(
            guid,
            name!,
            dataType,
            string.IsNullOrWhiteSpace(dataCategory) ? null : dataCategory,
            groupName,
            string.IsNullOrWhiteSpace(description) ? null : description);
    }

    private static int Col(string[]? header, string name, int fallback)
    {
        if (header is not null)
        {
            for (var i = 0; i < header.Length; i++)
            {
                if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return fallback;
    }

    private static string? Get(string[] parts, int index)
        => index >= 0 && index < parts.Length ? parts[index] : null;
}
