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
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* Intentional */ }
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
    Log($"Marker size: {json.Length} chars");

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var isMultiVersion = root.TryGetProperty("Artifacts", out _);

    if (isMultiVersion)
    {
        Log("Multi-version update detected.");
        ApplyMultiVersionUpdate(root);
    }
    else
    {
        Log("Single-version update detected.");
        ApplySingleVersionUpdate(json);
    }
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

void ApplyMultiVersionUpdate(JsonElement root)
{
    var artifacts = root.GetProperty("Artifacts");

    Log($"Found {artifacts.GetArrayLength()} artifact(s) to apply.");

    Log("Waiting for Revit to exit...");
    WaitForRevitExit();

    int totalCopied = 0;
    int totalFailed = 0;

    foreach (var artifact in artifacts.EnumerateArray())
    {
        var stagingPath = artifact.GetProperty("StagingPath").GetString() ?? "";
        var targetPath = artifact.GetProperty("TargetInstallPath").GetString() ?? "";
        var tag = artifact.TryGetProperty("ArtifactTag", out var tagEl) ? tagEl.GetString() ?? "" : "";

        Log($"Processing artifact [{tag}]:");
        Log($"  Staging: {stagingPath}");
        Log($"  Target:  {targetPath}");

        if (!Directory.Exists(stagingPath))
        {
            Log($"  Staging directory not found. Skipping.");
            continue;
        }

        var stagingFiles = Directory.GetFiles(stagingPath);
        Log($"  Staging contains {stagingFiles.Length} files");

        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
            Log($"  Created target directory.");
        }

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

            var dest = Path.Combine(targetPath, fileName);
            try
            {
                File.Copy(file, dest, overwrite: true);
                totalCopied++;
                Log($"  OK: {fileName}");
            }
            catch (Exception ex)
            {
                totalFailed++;
                Log($"  FAIL: {fileName} - {ex.Message}");
            }
        }
    }

    QueueUpdaterSelfUpdate(appData, smartConDir);

    CleanupStaging(smartConDir);

    File.Delete(pendingPath);
    Log($"Deleted update-pending.json marker.");

    Log($"=== Multi-version update complete ({totalCopied} files copied, {totalFailed} failed) ===");
}

void ApplySingleVersionUpdate(string json)
{
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
        Log($"Staging directory not found. Deleting marker.");
        File.Delete(pendingPath);
        return;
    }

    var stagingFiles = Directory.GetFiles(pending.StagingPath);
    Log($"Staging contains {stagingFiles.Length} files");

    WaitForRevitExit();

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
            Log($"  OK: {fileName}");
        }
        catch (Exception ex)
        {
            failedFiles++;
            Log($"  FAIL: {fileName} - {ex.Message}");
        }
    }

    Log($"Copied {copiedFiles} files, {failedFiles} failed.");

    QueueUpdaterSelfUpdate(appData, smartConDir);

    CleanupStaging(smartConDir);

    File.Delete(pendingPath);
    Log("Deleted update-pending.json marker.");

    Log($"=== Update complete ({copiedFiles} files copied) ===");
}

void WaitForRevitExit()
{
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
        Log("Timeout waiting for Revit to exit. Proceeding anyway.");
    }
}

void QueueUpdaterSelfUpdate(string appDataDir, string scDir)
{
    var updaterSelfUpdateDir = Path.Combine(scDir, "updater-pending");

    var allStagingDirs = new List<string>();

    try
    {
        var multiPending = JsonSerializer.Deserialize<MultiVersionPendingInfo>(
            File.ReadAllText(pendingPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (multiPending?.Artifacts != null)
        {
            foreach (var a in multiPending.Artifacts)
            {
                if (Directory.Exists(a.StagingPath))
                    allStagingDirs.Add(a.StagingPath);
            }
        }
    }
    catch { /* Fall through to single-version */ }

    if (allStagingDirs.Count == 0)
    {
        try
        {
            var singlePending = JsonSerializer.Deserialize<PendingUpdateInfo>(
                File.ReadAllText(pendingPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (singlePending?.StagingPath != null && Directory.Exists(singlePending.StagingPath))
                allStagingDirs.Add(singlePending.StagingPath);
        }
        catch { /* No staging dirs */ }
    }

    foreach (var stagingDir in allStagingDirs)
    {
        foreach (var file in Directory.GetFiles(stagingDir))
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
    }
}

void CleanupStaging(string scDir)
{
    try { Directory.Delete(Path.Combine(scDir, "staging"), true); Log("Deleted staging directory."); }
    catch (Exception ex) { Log($"Could not delete staging: {ex.Message}"); }
}

internal record PendingUpdateInfo(string StagingPath, string TargetInstallPath);
internal record MultiVersionPendingInfo(List<ArtifactInfo>? Artifacts);
internal record ArtifactInfo(string StagingPath, string TargetInstallPath, string ArtifactTag);
