using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Updates;

public sealed partial class GitHubUpdateService
{
    public async Task StageUpdateAsync(string zipPath)
    {
        var neededTags = GetNeededArtifactTags();
        var allAssets = await FetchAllAssetsFromLatestRelease(_settingsRepo.Load()).ConfigureAwait(false);

        var artifacts = new List<StagedArtifact>();

        foreach (var tag in neededTags)
        {
            if (!allAssets.TryGetValue(tag, out var asset))
                continue;

            var zipFileName = asset.Name;
            var fullZipPath = Path.Combine(s_stagingDir, zipFileName);
            if (!File.Exists(fullZipPath))
                continue;

            var extractDir = Path.Combine(s_stagingDir, $"extracted-{tag}");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);

            ZipFile.ExtractToDirectory(fullZipPath, extractDir
#if !NETFRAMEWORK
                , overwriteFiles: true
#endif
            );

            var targetDir = GetTargetInstallPath(tag);

            artifacts.Add(new StagedArtifact(
                StagingPath: extractDir,
                TargetInstallPath: targetDir,
                ArtifactTag: tag
            ));
        }

        var pending = new MultiVersionPendingUpdate(
            Version: "staged",
            StagedAt: DateTime.Now,
            Artifacts: artifacts
        );

        var json = JsonSerializer.Serialize(pending, s_jsonOptions);
#if NETFRAMEWORK
        File.WriteAllText(s_pendingMarkerPath, json);
#else
        await File.WriteAllTextAsync(s_pendingMarkerPath, json).ConfigureAwait(false);
#endif
    }

    public async Task<PendingUpdate?> GetPendingUpdateAsync()
    {
        if (!File.Exists(s_pendingMarkerPath))
            return null;

#if NETFRAMEWORK
        var json = File.ReadAllText(s_pendingMarkerPath);
#else
        var json = await File.ReadAllTextAsync(s_pendingMarkerPath).ConfigureAwait(false);
#endif
        return JsonSerializer.Deserialize<PendingUpdate>(json, s_jsonOptions);
    }

    public async Task<MultiVersionPendingUpdate?> GetMultiVersionPendingUpdateAsync()
    {
        if (!File.Exists(s_pendingMarkerPath))
            return null;

#if NETFRAMEWORK
        var json = File.ReadAllText(s_pendingMarkerPath);
#else
        var json = await File.ReadAllTextAsync(s_pendingMarkerPath).ConfigureAwait(false);
#endif
        return JsonSerializer.Deserialize<MultiVersionPendingUpdate>(json, s_jsonOptions);
    }

    public async Task ApplyPendingUpdateAsync()
    {
        var multi = await GetMultiVersionPendingUpdateAsync().ConfigureAwait(false);
        if (multi is not null)
        {
            foreach (var artifact in multi.Artifacts)
            {
                if (!Directory.Exists(artifact.StagingPath)) continue;

                Directory.CreateDirectory(artifact.TargetInstallPath);

                foreach (var file in Directory.GetFiles(artifact.StagingPath))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".dll" or ".exe")) continue;
                    var dest = Path.Combine(artifact.TargetInstallPath, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                }
            }

            foreach (var artifact in multi.Artifacts)
            {
                try
                {
                    if (Directory.Exists(artifact.StagingPath))
                        Directory.Delete(artifact.StagingPath, true);
                }
                catch { /* Intentional: staging cleanup */ }
            }

            File.Delete(s_pendingMarkerPath);
            return;
        }

        var pending = await GetPendingUpdateAsync().ConfigureAwait(false);
        if (pending is null) return;

        if (!Directory.Exists(pending.StagingPath))
        {
            File.Delete(s_pendingMarkerPath);
            return;
        }

        var targetDir = pending.TargetInstallPath;
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(pending.StagingPath, "*.dll"))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var file in Directory.GetFiles(pending.StagingPath, "*.exe"))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        Directory.Delete(pending.StagingPath, true);
        File.Delete(s_pendingMarkerPath);
    }

    private async Task<Dictionary<string, AssetInfo>> FetchAllAssetsFromLatestRelease(
        Core.Models.UpdateSettings settings)
    {
        // If prereleases are enabled, fetch all releases and pick the latest (including prereleases)
        // Otherwise use the /releases/latest endpoint which returns only stable releases
        var url = settings.IncludePrerelease
            ? $"https://api.github.com/repos/{settings.GitHubOwner}/{settings.GitHubRepo}/releases?per_page=1"
            : $"https://api.github.com/repos/{settings.GitHubOwner}/{settings.GitHubRepo}/releases/latest";

        if (!string.IsNullOrEmpty(settings.GitHubToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.GitHubToken);

        var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (settings.IncludePrerelease)
        {
            using var doc = JsonDocument.Parse(json);
            var releases = doc.RootElement;
            if (releases.GetArrayLength() == 0)
                return new Dictionary<string, AssetInfo>(StringComparer.OrdinalIgnoreCase);
            return ParseAllAssets(releases[0].GetProperty("assets"));
        }
        else
        {
            using var doc = JsonDocument.Parse(json);
            return ParseAllAssets(doc.RootElement.GetProperty("assets"));
        }
    }

    private static Dictionary<string, AssetInfo> ParseAllAssets(JsonElement assets)
    {
        var result = new Dictionary<string, AssetInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            var tag = ExtractArtifactTag(name);
            if (tag is null) continue;

            if (!result.ContainsKey(tag))
            {
                result[tag] = new AssetInfo(
                    Name: name,
                    DownloadUrl: asset.GetProperty("browser_download_url").GetString()!,
                    Size: asset.GetProperty("size").GetInt64()
                );
            }
        }

        return result;
    }

    private static string BuildNetworkErrorMessage(Core.Models.UpdateSettings settings, Exception ex)
    {
        var detail = ex.InnerException?.Message ?? ex.Message;
        return string.Format(
            LocalizationService.GetString("About_NetworkError"),
            $"{settings.GitHubOwner}/{settings.GitHubRepo}: {detail}");
    }

    private static string? ExtractArtifactTag(string assetName)
    {
        // -R26. отсутствовал до #233-инфраструктуры: R26-архивы релизов никогда
        // не распознавались апдейтером (zip молча игнорировался) — исправлено.
        var patterns = new[] { "-R19.", "-R20.", "-R21.", "-R22.", "-R23.", "-R24.", "-R25.", "-R26.", "-R27." };
        foreach (var pattern in patterns)
        {
#if NET8_0
            if (assetName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
#else
#pragma warning disable CA2249 // net48: Contains(string, StringComparison) unavailable
            if (assetName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
#pragma warning restore CA2249
#endif
            {
                var tag = pattern.TrimStart('-').TrimEnd('.');
                if (tag is "R20")
                    return "R19";
                if (tag is "R22" or "R23")
                    return "R21";
                return tag;
            }
        }
        return null;
    }
}
