using System.Text;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// One PARAM row of a Revit shared parameter (.txt) file.
/// </summary>
public sealed record AgentSharedParameterFileEntry(
    string Group,
    string Name,
    string Guid,
    string DataType,
    bool Visible,
    bool UserModifiable,
    string? Description);

/// <summary>
/// Reads a Revit shared parameter file AS TEXT without touching the active
/// file: Revit's API only exposes <c>OpenSharedParameterFile()</c> for the
/// ACTIVE file, and switching it is a global user setting that never rolls
/// back. The format is a tab-separated text with META/GROUP/PARAM blocks
/// (Autodesk API guide, "Definition File"); Revit writes UTF-16 for some
/// locales, so the encoding is detected from the BOM (live feedback v17, 1.4).
/// </summary>
public static class AgentSharedParameterFileText
{
    public static bool TryParseFile(
        string path,
        out IReadOnlyList<AgentSharedParameterFileEntry> entries,
        out int groupCount,
        out string? error)
    {
        entries = Array.Empty<AgentSharedParameterFileEntry>();
        groupCount = 0;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "File not found: " + path;
                return false;
            }

            var content = ReadWithBomDetection(path);
            return TryParse(content, out entries, out groupCount, out error);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TryParse(
        string content,
        out IReadOnlyList<AgentSharedParameterFileEntry> entries,
        out int groupCount,
        out string? error)
    {
        entries = Array.Empty<AgentSharedParameterFileEntry>();
        groupCount = 0;
        error = null;
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = new List<AgentSharedParameterFileEntry>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim('\uFEFF');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;

            var keyword = parts[0].Trim();
            if (string.Equals(keyword, "GROUP", StringComparison.OrdinalIgnoreCase))
            {
                // GROUP <id> <name>
                if (parts.Length >= 3)
                {
                    groups[parts[1].Trim()] = parts[2].Trim();
                }

                continue;
            }

            if (!string.Equals(keyword, "PARAM", StringComparison.OrdinalIgnoreCase))
                continue;

            // PARAM <guid> <name> <datatype> <datacategory> <group> <visible>
            //      <description> <usermodifiable> — the last fields are optional
            // in files written by older Revit versions.
            if (parts.Length < 6)
                continue;

            var guid = parts[1].Trim();
            var name = parts[2].Trim();
            var dataType = parts[3].Trim();
            var groupId = parts[5].Trim();
            var visible = parts.Length > 6 && parts[6].Trim() == "1";
            var description = parts.Length > 7 ? parts[7].Trim() : null;
            var userModifiable = parts.Length <= 8 || parts[8].Trim() == "1";
            parsed.Add(new AgentSharedParameterFileEntry(
                groups.TryGetValue(groupId, out var groupName) ? groupName : groupId,
                name,
                guid,
                dataType,
                visible,
                userModifiable,
                string.IsNullOrEmpty(description) ? null : description));
        }

        groupCount = groups.Count;
        entries = parsed;
        if (parsed.Count == 0 && groups.Count == 0)
        {
            error = "The file has no GROUP/PARAM entries — is it a Revit shared parameter file?";
            return false;
        }

        return true;
    }

    private static string ReadWithBomDetection(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }
}
