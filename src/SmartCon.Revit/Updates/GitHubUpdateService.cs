using System.IO.Compression;
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

    private static readonly string s_stagingDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon", "staging");

    private static readonly string s_pendingMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon", "update-pending.json");

    private string? _cachedVersion;

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
        var url = $"https://api.github.com/repos/{settings.GitHubOwner}/{settings.GitHubRepo}/releases/latest";

        if (!string.IsNullOrEmpty(settings.GitHubToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.GitHubToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url);
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

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "";
        var version = tagName.TrimStart('v');

        if (!IsNewer(version, GetCurrentVersion()))
            return null;

        var assets = root.GetProperty("assets");
        var zipAsset = FindZipAsset(assets);

        if (zipAsset is null)
            return null;

        return new UpdateInfo(
            Version: version,
            TagName: tagName,
            ReleaseNotes: root.TryGetProperty("body", out var body) ? body.GetString() : null,
            PublishedAt: root.GetProperty("published_at").GetDateTime(),
            DownloadUrl: zipAsset.Value.browser_download_url,
            FileSize: zipAsset.Value.size,
            AssetName: zipAsset.Value.name
        );
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(s_stagingDir);

        var settings = _settingsRepo.Load();
        if (!string.IsNullOrEmpty(settings.GitHubToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.GitHubToken);

        var zipPath = Path.Combine(s_stagingDir, info.AssetName);
        using var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? info.FileSize;
        var buffer = new byte[81920];
        long bytesRead = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(zipPath);

        int read;
        while ((read = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            bytesRead += read;
            progress?.Report((double)bytesRead / totalBytes);
        }

        return zipPath;
    }

    public async Task StageUpdateAsync(string zipPath)
    {
        var extractDir = Path.Combine(s_stagingDir, "extracted");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);

        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var installPath = GetInstallPath();
        var pending = new PendingUpdate(
            Version: "staged",
            StagingPath: extractDir,
            StagedAt: DateTime.Now,
            TargetInstallPath: installPath
        );

        var json = JsonSerializer.Serialize(pending, s_jsonOptions);
        await File.WriteAllTextAsync(s_pendingMarkerPath, json);
    }

    public async Task<PendingUpdate?> GetPendingUpdateAsync()
    {
        if (!File.Exists(s_pendingMarkerPath))
            return null;

        var json = await File.ReadAllTextAsync(s_pendingMarkerPath);
        return JsonSerializer.Deserialize<PendingUpdate>(json, s_jsonOptions);
    }

    public async Task ApplyPendingUpdateAsync()
    {
        var pending = await GetPendingUpdateAsync();
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
        return dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2025", "SmartCon");
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

    private static (string browser_download_url, long size, string name)? FindZipAsset(JsonElement assets)
    {
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64(),
                    name
                );
            }
        }
        return null;
    }
}
