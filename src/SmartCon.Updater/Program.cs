using System.Diagnostics;
using System.Text.Json;

var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var smartConDir = Path.Combine(appData, "SmartCon");
Directory.CreateDirectory(smartConDir);
var logPath = Path.Combine(smartConDir, "updater-log.txt");
var pendingPath = Path.Combine(smartConDir, "update-pending.json");

void Log(string msg)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
    Console.WriteLine(line);
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* Intentional: log write failure — nowhere to report */ }
}

Log("=== SmartCon.Updater started ===");

try
{
    if (!File.Exists(pendingPath))
    {
        Log("No pending update found. Exiting.");
        return;
    }

    Log("Pending update detected. Reading marker...");

    var json = File.ReadAllText(pendingPath);
    Log($"Marker content: {json}");

    var pending = JsonSerializer.Deserialize<PendingUpdateInfo>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (pending is null)
    {
        Log("Failed to deserialize marker. Deleting.");
        File.Delete(pendingPath);
        return;
    }

    Log($"StagingPath: {pending.StagingPath}");
    Log($"TargetInstallPath: {pending.TargetInstallPath}");

    if (!Directory.Exists(pending.StagingPath))
    {
        Log($"Staging directory not found: {pending.StagingPath}. Deleting marker.");
        File.Delete(pendingPath);
        return;
    }

    var stagingFiles = Directory.GetFiles(pending.StagingPath);
    Log($"Staging contains {stagingFiles.Length} files:");
    foreach (var f in stagingFiles) Log($"  {Path.GetFileName(f)}");

    Log("Waiting for Revit to exit...");

    var waitMs = 0;
    const int maxWaitMs = 60_000;
    while (waitMs < maxWaitMs)
    {
        var revitProcesses = Process.GetProcessesByName("Revit");
        if (revitProcesses.Length == 0)
        {
            Log("Revit exited.");
            break;
        }

        foreach (var p in revitProcesses) p.Dispose();
        Thread.Sleep(1000);
        waitMs += 1000;
    }

    if (waitMs >= maxWaitMs)
    {
        Log("Timeout waiting for Revit to exit. Aborting.");
        return;
    }

    Log($"Applying update to: {pending.TargetInstallPath}");

    if (!Directory.Exists(pending.TargetInstallPath))
    {
        Directory.CreateDirectory(pending.TargetInstallPath);
        Log("Created target directory.");
    }

    int copiedFiles = 0;
    int failedFiles = 0;
    foreach (var file in stagingFiles)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".dll" or ".exe"))
            continue;

        var fileName = Path.GetFileName(file);
        if (fileName.StartsWith("SmartCon.Updater.", StringComparison.OrdinalIgnoreCase))
        {
            Log($"  SKIP: {fileName} (cannot update self while running)");
            continue;
        }

        var dest = Path.Combine(pending.TargetInstallPath, fileName);
        try
        {
            File.Copy(file, dest, overwrite: true);
            copiedFiles++;
            Log($"  OK: {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            failedFiles++;
            Log($"  FAIL: {Path.GetFileName(file)} - {ex.Message}");
        }
    }

    Log($"Copied {copiedFiles} files, {failedFiles} failed.");

    var updaterSelfUpdateDir = Path.Combine(appData, "SmartCon", "updater-pending");
    foreach (var file in stagingFiles)
    {
        var fileName = Path.GetFileName(file);
        if (!fileName.StartsWith("SmartCon.Updater.", StringComparison.OrdinalIgnoreCase))
            continue;

        if (!Directory.Exists(updaterSelfUpdateDir))
            Directory.CreateDirectory(updaterSelfUpdateDir);

        var pendingSelf = Path.Combine(updaterSelfUpdateDir, fileName);
        try
        {
            File.Copy(file, pendingSelf, overwrite: true);
            Log($"  SELF-QUEUED: {fileName} -> will update on next launch");
        }
        catch (Exception ex)
        {
            Log($"  SELF-QUEUE FAIL: {fileName} - {ex.Message}");
        }
    }

    try { Directory.Delete(pending.StagingPath, true); Log("Deleted staging extracted dir."); }
    catch (Exception ex) { Log($"Could not delete staging: {ex.Message}"); }

    var stagingDir = Path.Combine(appData, "SmartCon", "staging");
    if (Directory.Exists(stagingDir))
    {
        try { Directory.Delete(stagingDir, true); Log("Deleted staging root."); }
        catch (Exception ex) { Log($"Could not delete staging root: {ex.Message}"); }
    }

    File.Delete(pendingPath);
    Log("Deleted update-pending.json marker.");

    Log($"=== Update complete ({copiedFiles} files copied) ===");
}
catch (Exception ex)
{
    Log($"FATAL ERROR: {ex}");
}
finally
{
    Log("Updater exiting in 5 seconds...");
    Thread.Sleep(5000);
}

internal record PendingUpdateInfo(string StagingPath, string TargetInstallPath);
