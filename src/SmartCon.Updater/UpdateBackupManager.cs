namespace SmartCon.Updater;

/// <summary>
/// Pre-update backup of the install folder (ADR-051). Before overwriting, the current
/// folder is copied to %APPDATA%\SmartCon\backup\{folderName}-{timestamp}. Only the
/// last 3 backups per folder are kept. Rollback = copy the backup contents back.
/// </summary>
internal static class UpdateBackupManager
{
    private const int KeepCount = 3;

    public static string? CreateBackup(string targetDir, string backupRoot, Action<string> log)
    {
        if (!Directory.Exists(targetDir))
            return null;

        try
        {
            var folderName = new DirectoryInfo(targetDir.TrimEnd(Path.DirectorySeparatorChar)).Name;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupDir = Path.Combine(backupRoot, $"{folderName}-{stamp}");
            CopyDirectory(targetDir, backupDir);
            log($"  BACKUP: {targetDir} -> {backupDir}");
            PruneBackups(backupRoot, folderName, log);
            return backupDir;
        }
        catch (Exception ex)
        {
            log($"  BACKUP FAIL: {targetDir} - {ex.Message} (update continues without backup)");
            return null;
        }
    }

    private static void PruneBackups(string backupRoot, string folderName, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(backupRoot))
                return;

            var stale = Directory.GetDirectories(backupRoot, $"{folderName}-*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Skip(KeepCount)
                .ToList();

            foreach (var dir in stale)
            {
                Directory.Delete(dir, recursive: true);
                log($"  BACKUP PRUNE: {dir}");
            }
        }
        catch (Exception ex)
        {
            log($"  BACKUP PRUNE FAIL: {ex.Message}");
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
