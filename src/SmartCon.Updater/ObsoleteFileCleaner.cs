namespace SmartCon.Updater;

/// <summary>
/// Parses and applies obsolete-files.txt (ADR-051): the list of files that must be
/// deleted from the install folder during an update (e.g. third-party dlls that are
/// ILRepack-merged into SmartCon.Dependencies.dll since v2.1).
/// Entries are plain file names only — anything with path separators or non-dll
/// extensions is rejected to prevent directory traversal.
/// </summary>
internal static class ObsoleteFileCleaner
{
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines)
    {
        return lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Where(IsSafeFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int DeleteFrom(string targetDir, IEnumerable<string> fileNames, Action<string> log)
    {
        var deleted = 0;
        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(targetDir, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                File.Delete(path);
                deleted++;
                log($"  OBSOLETE: deleted {fileName}");
            }
            catch (Exception ex)
            {
                log($"  OBSOLETE FAIL: {fileName} - {ex.Message}");
            }
        }
        return deleted;
    }

    private static bool IsSafeFileName(string name)
    {
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !name.Contains(Path.DirectorySeparatorChar)
            && !name.Contains(Path.AltDirectorySeparatorChar)
            && !name.StartsWith('.')
            && !char.IsWhiteSpace(name[0]);
    }
}
