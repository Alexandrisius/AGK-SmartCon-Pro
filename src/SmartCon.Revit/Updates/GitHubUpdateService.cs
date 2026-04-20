using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Updates;

public sealed class GitHubUpdateService : IUpdateService
{
    private readonly IUpdateSettingsRepository _settingsRepo;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string s_appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string s_smartConDir = Path.Combine(s_appDataDir, "SmartCon");
    private static readonly string s_stagingDir = Path.Combine(s_smartConDir, "staging");
    private static readonly string s_pendingMarkerPath = Path.Combine(s_smartConDir, "update-pending.json");

    private string? _cachedVersion;

    private static readonly int[] s_supportedRevitVersions = [2019, 2020, 2021, 2022, 2023, 2024, 2025, 2026];

    public GitHubUpdateService(IUpdateSettingsRepository settingsRepo)
    {
        _settingsRepo = settingsRepo;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SmartCon-UpdateService");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string GetCurrentVersion()
    {
        if (_cachedVersion is not null) return _cachedVersion;

        var attr = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr is not null && !string.IsNullOrWhiteSpace(attr.InformationalVersion))
        {
            _cachedVersion = attr.InformationalVersion.Split('+')[0].Trim();
            return _cachedVersion;
        }

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        _cachedVersion = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        return _cachedVersion;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        var settings = _settingsRepo.Load();
        var url = $"https://api.github.com/repos/{settings.GitHubOwner}/{settings.GitHubRepo}/releases?per_page=20";

        if (!string.IsNullOrEmpty(settings.GitHubToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.GitHubToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Network error checking updates ({settings.GitHubOwner}/{settings.GitHubRepo}): {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"Repository or releases not found: {settings.GitHubOwner}/{settings.GitHubRepo}. " +
                    "Check UpdateSettings (GitHubOwner / GitHubRepo).");
            throw new InvalidOperationException(
                $"GitHub API returned {response.StatusCode} for {settings.GitHubOwner}/{settings.GitHubRepo}.");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var releases = doc.RootElement;

        var currentVersion = GetCurrentVersion();

        var newerReleases = new List<(string Version, string TagName, string? Body, DateTime PublishedAt, JsonElement Assets)>();

        foreach (var release in releases.EnumerateArray())
        {
            var tagName = release.GetProperty("tag_name").GetString() ?? "";
            var version = tagName.TrimStart('v');

            if (!IsNewer(version, currentVersion)) continue;

            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

            var body = release.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            var publishedAt = release.GetProperty("published_at").GetDateTime();

            newerReleases.Add((version, tagName, body, publishedAt, release.GetProperty("assets")));
        }

        if (newerReleases.Count == 0)
            return null;

        newerReleases.Sort((a, b) =>
        {
            if (!TryParseVersion(a.Version, out var va)) va = new Version(0, 0, 0);
            if (!TryParseVersion(b.Version, out var vb)) vb = new Version(0, 0, 0);
            return va.CompareTo(vb);
        });

        var latest = newerReleases[^1];

        var allAssets = ParseAllAssets(latest.Assets);
        if (allAssets.Count == 0)
            return null;

        var primaryAsset = allAssets.Values.First();

        var changelog = BuildChangelog(newerReleases);

        return new UpdateInfo(
            Version: latest.Version,
            TagName: latest.TagName,
            ReleaseNotes: latest.Body,
            PublishedAt: latest.PublishedAt,
            DownloadUrl: primaryAsset.DownloadUrl,
            FileSize: primaryAsset.Size,
            AssetName: primaryAsset.Name,
            Changelog: changelog
        );
    }

    private static string BuildChangelog(
        List<(string Version, string TagName, string? Body, DateTime PublishedAt, JsonElement Assets)> releases)
    {
        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < releases.Count; i++)
        {
            var r = releases[i];

            if (sb.Length > 0)
                sb.AppendLine().AppendLine("─────────────────────────────────").AppendLine();

            sb.AppendLine($"v{r.Version}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(r.Body))
                sb.AppendLine(r.Body!.Trim());
            else
                sb.AppendLine("(no release notes)");
        }

        return sb.ToString();
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(s_stagingDir);

        var settings = _settingsRepo.Load();
        if (!string.IsNullOrEmpty(settings.GitHubToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.GitHubToken);

        var neededTags = GetNeededArtifactTags();

        var allAssets = await FetchAllAssetsFromLatestRelease(settings).ConfigureAwait(false);
        var totalSize = 0L;
        var toDownload = new List<(string Tag, AssetInfo Asset)>();
        foreach (var tag in neededTags)
        {
            if (allAssets.TryGetValue(tag, out var asset))
            {
                toDownload.Add((tag, asset));
                totalSize += asset.Size;
            }
        }

        if (toDownload.Count == 0)
            throw new InvalidOperationException("No suitable update assets found on GitHub Release.");

        var lastZipPath = "";
        long totalBytesRead = 0;

        foreach (var (tag, asset) in toDownload)
        {
            var zipPath = Path.Combine(s_stagingDir, asset.Name);
            using var response = await _httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bufferSize = 81920;
            var buffer = new byte[bufferSize];

#if NETFRAMEWORK
            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = File.Create(zipPath);

            int read;
            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
#else
            await using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var fileStream = File.Create(zipPath);

            int read;
            while ((read = await contentStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
#endif
                totalBytesRead += read;
                progress?.Report((double)totalBytesRead / totalSize);
            }

            lastZipPath = zipPath;
        }

        return lastZipPath;
    }

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

    private static string GetInstallPath()
    {
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var dir = Path.GetDirectoryName(assemblyLocation);
        return dir ?? Path.Combine(s_appDataDir, "SmartCon", "2025");
    }

    internal static string GetTargetInstallPath(string artifactTag)
    {
        return artifactTag switch
        {
            "R19" => Path.Combine(s_smartConDir, "2019-2020"),
            "R21" => Path.Combine(s_smartConDir, "2021-2023"),
            "R24" => Path.Combine(s_smartConDir, "2024"),
            "R25" => Path.Combine(s_smartConDir, "2025"),
            "R26" => Path.Combine(s_smartConDir, "2026"),
            _ => Path.Combine(s_smartConDir, artifactTag)
        };
    }

    internal static HashSet<string> GetNeededArtifactTags()
    {
        var installed = DetectInstalledRevitVersions();
        var tags = new HashSet<string>();

        if (installed.Contains(2019) || installed.Contains(2020))
            tags.Add("R19");
        if (installed.Contains(2021) || installed.Contains(2022) || installed.Contains(2023))
            tags.Add("R21");
        if (installed.Contains(2024))
            tags.Add("R24");
        if (installed.Contains(2025))
            tags.Add("R25");
        if (installed.Contains(2026))
            tags.Add("R26");

        if (tags.Count == 0)
            tags = ["R19", "R21", "R24", "R25", "R26"];

        return tags;
    }

    internal static HashSet<int> DetectInstalledRevitVersions()
    {
        var result = new HashSet<int>();

        foreach (var version in s_supportedRevitVersions)
        {
            try
            {
                var key = $@"SOFTWARE\Autodesk\Revit\Autodesk Revit {version}";
                using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (regKey?.GetValue("InstallLocation") is string installPath
                    && !string.IsNullOrWhiteSpace(installPath)
                    && Directory.Exists(installPath))
                {
                    result.Add(version);
                }
            }
            catch { /* Registry access may fail */ }
        }

        return result;
    }

    private static bool IsNewer(string remote, string current)
    {
        if (!TryParseVersion(remote, out var r)) return false;
        if (!TryParseVersion(current, out var c)) return false;
        return r > c;
    }

    private static bool TryParseVersion(string v, out Version version)
    {
        var parts = v.Split('.');
        var normalized = parts.Length switch
        {
            2 => $"{parts[0]}.{parts[1]}.0",
            1 => $"{parts[0]}.0.0",
            _ => $"{parts[0]}.{parts[1]}.{parts[2]}"
        };
        return Version.TryParse(normalized, out version!);
    }

    private async Task<Dictionary<string, AssetInfo>> FetchAllAssetsFromLatestRelease(
        Core.Models.UpdateSettings settings)
    {
        var url = $"https://api.github.com/repos/{settings.GitHubOwner}/{settings.GitHubRepo}/releases/latest";

        if (!string.IsNullOrEmpty(settings.GitHubToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.GitHubToken);

        var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return ParseAllAssets(doc.RootElement.GetProperty("assets"));
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

    private static string? ExtractArtifactTag(string assetName)
    {
        var patterns = new[] { "-R19.", "-R20.", "-R21.", "-R22.", "-R23.", "-R24.", "-R25." };
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

    private record AssetInfo(string Name, string DownloadUrl, long Size);
}
