using System.IO;
using System.Text;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Removes temp staging folders created during the
/// "Import Active File" command. Replaces the static
/// <c>CleanupImportActiveTemp</c> helper that used to live in
/// <c>FamilyManagerMainViewModel.FamilyEdit.cs</c>.
///
/// Cleanup scope:
/// <list type="bullet">
///   <item><c>%TEMP%\SmartCon\FMLoad\*</c> — created by <see cref="ActiveFamilyFilePreparer"/> for active <c>.rfa</c> imports (sub-folder per import).</item>
///   <item><c>%TEMP%\SmartCon\SystemFamilyLoadFromProject\*</c> — created by <see cref="SystemFamilyRevitOperations"/> for active <c>.rvt</c> imports (sub-folder per category).</item>
/// </list>
///
/// The service is defensive:
/// <list type="bullet">
///   <item>missing root / sub-folder is a no-op, not an error;</item>
///   <item>individual file/dir delete failures are logged as warnings, not propagated;</item>
///   <item>cleanup is idempotent — calling it twice produces the same result;</item>
///   <item>cleanup is bounded by a caller-supplied <see cref="CancellationToken"/>.</item>
/// </list>
/// </summary>
internal sealed class ActiveImportCleanupService : IActiveImportCleanupService
{
    /// <summary>
    /// Sub-folders under <c>%TEMP%\SmartCon</c> that host temp staging
    /// artefacts for the active-file import pipeline. Each entry is a
    /// sub-folder; its child directories (one per import) are removed
    /// recursively.
    /// </summary>
    private static readonly string[] StagingSubdirs =
        ["FMLoad", SystemFamilyTempLayout.StagingSubdir];

    public Task CleanupAfterImportAsync(CancellationToken ct = default)
    {
        return Task.Run(
            () => CleanupImpl(Path.Combine(Path.GetTempPath(), SystemFamilyTempLayout.TempRoot), ct),
            ct);
    }

    /// <summary>
    /// Internal seam for unit tests. Cleans every staging sub-folder under
    /// <paramref name="rootPath"/> and logs a structured before/after summary.
    /// </summary>
    internal void CleanupImpl(string rootPath, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("ActiveCleanup",
            ("Method", "CleanupImpl"),
            ("RootPath", rootPath));
        var startUtc = DateTimeOffset.UtcNow;

        try
        {
            if (!Directory.Exists(rootPath))
            {
                SmartConLogger.Debug($"Temp root missing ('{rootPath}') — nothing to clean");
                return;
            }

            var locationsScanned = 0;
            var directoriesDeleted = 0;
            var directoriesSkipped = 0;
            var bytesReclaimed = 0L;
            var filesSkipped = new List<string>();

            foreach (var sub in StagingSubdirs)
            {
                ct.ThrowIfCancellationRequested();
                var dir = Path.Combine(rootPath, sub);
                if (!Directory.Exists(dir))
                {
                    SmartConLogger.Debug($"Skipped: '{dir}' does not exist");
                    continue;
                }

                locationsScanned++;
                SmartConLogger.Info($"Scanning '{dir}'");

                var childDirs = Directory.GetDirectories(dir);
                if (childDirs.Length == 0)
                {
                    SmartConLogger.Info(
                        $"'{dir}': empty (no staging sub-folders) — clean as a whistle");
                    continue;
                }

                SmartConLogger.Info(
                    $"'{dir}': found {childDirs.Length} staging sub-folder(s)");

                    foreach (var childDir in childDirs)
                    {
                        ct.ThrowIfCancellationRequested();
                        var childStart = DateTimeOffset.UtcNow;
                        var sizeBytes = SafeComputeDirectorySize(childDir, filesSkipped);
                        var fileCount = SafeCountFiles(childDir);

                        try
                        {
                            ClearReadOnlyAttributes(childDir);
                            Directory.Delete(childDir, recursive: true);
                            directoriesDeleted++;
                            bytesReclaimed += sizeBytes;
                            var childDurationMs = (long)(DateTimeOffset.UtcNow - childStart).TotalMilliseconds;
                            SmartConLogger.Info(
                                $"  ✓ Deleted '{Path.GetFileName(childDir)}' " +
                                $"(files={fileCount}, size={FormatBytes(sizeBytes)}, took {childDurationMs} ms)");
                        }
                        catch (Exception ex)
                        {
                            directoriesSkipped++;
                            SmartConLogger.Warn(
                                $"  ✗ Failed to delete '{childDir}': {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                var remaining = Directory.GetDirectories(dir);
                if (remaining.Length > 0)
                {
                    SmartConLogger.Warn(
                        $"'{dir}' still contains {remaining.Length} sub-folder(s) after cleanup: " +
                        $"[{string.Join(", ", remaining.Select(Path.GetFileName))}]");
                }
                else
                {
                    SmartConLogger.Info($"'{dir}' is clean (0 sub-folders remaining)");
                }
            }

            var totalDurationAll = (long)(DateTimeOffset.UtcNow - startUtc).TotalMilliseconds;
            var summary = new StringBuilder();
            summary.Append("=== summary: ");
            summary.Append($"locations_scanned={locationsScanned}, ");
            summary.Append($"directories_deleted={directoriesDeleted}, ");
            summary.Append($"directories_skipped={directoriesSkipped}, ");
            summary.Append($"files_skipped={filesSkipped.Count}, ");
            summary.Append($"bytes_reclaimed={bytesReclaimed} ({FormatBytes(bytesReclaimed)}), ");
            summary.Append($"took {totalDurationAll} ms");

            if (directoriesSkipped > 0 || filesSkipped.Count > 0)
            {
                SmartConLogger.Warn(summary.ToString());
            }
            else
            {
                SmartConLogger.Info(summary.ToString());
            }

            if (filesSkipped.Count > 0)
            {
                var preview = filesSkipped.Take(5).Select(Path.GetFileName);
                SmartConLogger.Warn(
                    $"Skipped files (first 5): [{string.Join(", ", preview)}]" +
                    (filesSkipped.Count > 5 ? $" (+{filesSkipped.Count - 5} more)" : ""));
            }
        }
        catch (OperationCanceledException)
        {
            SmartConLogger.Warn("Cleanup was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Cleanup pass failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort size of a directory. Files that cannot be stat'd
    /// (e.g. locked by Revit) are accumulated into <paramref name="skipped"/>
    /// and contribute 0 bytes.
    /// </summary>
    private static long SafeComputeDirectorySize(string dir, List<string> skipped)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex)
                {
                    skipped.Add(file);
                    SmartConLogger.Debug($"  ! Cannot stat '{file}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"  ! Cannot enumerate '{dir}': {ex.Message}");
        }
        return total;
    }

    private static int SafeCountFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Strips the <see cref="FileAttributes.ReadOnly"/> bit from every file
    /// under <paramref name="dir"/> so that the subsequent recursive delete
    /// can complete on Windows. Without this, files that we mark read-only
    /// in our own managed-storage pipeline (or that originated in a
    /// read-only source) survive the cleanup pass and become orphans.
    /// </summary>
    private static void ClearReadOnlyAttributes(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // Best-effort: if we can't clear the attribute we still
                    // let Directory.Delete try and report its own error.
                }
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
