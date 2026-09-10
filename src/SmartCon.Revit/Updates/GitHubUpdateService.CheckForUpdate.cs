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
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        var settings = _settingsRepo.Load();
        var url = $"https://api.github.com/repos/{settings.GitHubOwner}/{settings.GitHubRepo}/releases?per_page=20";

        if (!string.IsNullOrEmpty(settings.GitHubToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.GitHubToken);

        HttpResponseMessage response = null!;
        var lastTransient = (Exception?)null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                lastTransient = null;
                break;
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.AccessDenied)
            {
                // WSAEACCES (10013): ephemeral source port fell into a Windows-reserved range
                // (Hyper-V / winnat / Docker exclusion zones) or a leftover WFP rule from a
                // previous security product is blocking the connection. Retry — the next
                // attempt will get a different ephemeral port and may bypass the conflict.
                lastTransient = ex;
                if (attempt + 1 < MaxRetries)
                    await Task.Delay(TransientFaultDelayMs).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Non-transient HTTP failure: surface immediately, do not retry.
                throw;
            }
        }

        if (lastTransient is not null)
        {
            throw new InvalidOperationException(BuildNetworkErrorMessage(settings, lastTransient), lastTransient);
        }

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"Repository or releases not found: {settings.GitHubOwner}/{settings.GitHubRepo}. " +
                    "Check UpdateSettings (GitHubOwner / GitHubRepo).");

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remValues)
                    ? remValues.FirstOrDefault()
                    : null;
                var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
                    ? resetValues.FirstOrDefault()
                    : null;

                if (remaining == "0" && !string.IsNullOrEmpty(reset) && long.TryParse(reset, out var resetUnix))
                {
                    var resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix).LocalDateTime;
                    throw new InvalidOperationException(
                        $"GitHub API rate limit exceeded. Try again after {resetTime:HH:mm}. " +
                        "Consider adding a GitHub token in settings to increase the limit.");
                }

                throw new InvalidOperationException(
                    $"GitHub API access denied ({settings.GitHubOwner}/{settings.GitHubRepo}). " +
                    "If this happens frequently, add a GitHub token in settings.");
            }

            throw new InvalidOperationException(
                $"GitHub API returned {response.StatusCode} for {settings.GitHubOwner}/{settings.GitHubRepo}.");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var releases = doc.RootElement;

        var currentVersion = GetCurrentVersion();
        var currentIsPrerelease = SemVersion.TryParse(currentVersion, out var currentSemVer) && currentSemVer.IsPrerelease;
        var needDowngrade = currentIsPrerelease && !settings.IncludePrerelease;

        var candidateReleases = new List<(string Version, string TagName, string? Body, DateTime PublishedAt, bool IsPrerelease, JsonElement Assets)>();

        foreach (var release in releases.EnumerateArray())
        {
            var tagName = release.GetProperty("tag_name").GetString() ?? "";
            var version = tagName.TrimStart('v');

            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

            var isPrerelease = release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();

            // Skip prereleases unless explicitly enabled
            if (isPrerelease && !settings.IncludePrerelease) continue;

            if (needDowngrade)
            {
                // User is on prerelease but wants stable: accept any stable release
                // (version number may be lower — that's the point of downgrade)
                if (isPrerelease) continue;
            }
            else
            {
                if (!IsNewer(version, currentVersion)) continue;
            }

            var body = release.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            var publishedAt = release.GetProperty("published_at").GetDateTime();

            candidateReleases.Add((version, tagName, body, publishedAt, isPrerelease, release.GetProperty("assets")));
        }

        if (candidateReleases.Count == 0)
            return null;

        candidateReleases.Sort((a, b) =>
        {
            if (!SemVersion.TryParse(a.Version, out var va)) va = new SemVersion(0, 0, 0);
            if (!SemVersion.TryParse(b.Version, out var vb)) vb = new SemVersion(0, 0, 0);
            return va.CompareTo(vb);
        });

        var latest = candidateReleases[^1];

        var allAssets = ParseAllAssets(latest.Assets);
        if (allAssets.Count == 0)
            return null;

        var primaryAsset = allAssets.Values.First();

        var changelogReleases = needDowngrade
            ? new List<(string Version, string TagName, string? Body, DateTime PublishedAt, bool IsPrerelease, JsonElement Assets)> { latest }
            : candidateReleases;
        var changelog = BuildChangelog(changelogReleases, needDowngrade);

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
        List<(string Version, string TagName, string? Body, DateTime PublishedAt, bool IsPrerelease, JsonElement Assets)> releases,
        bool isDowngrade)
    {
        var sb = new System.Text.StringBuilder();

        if (isDowngrade)
        {
            sb.AppendLine("⚠️  DOWNGRADE FROM BETA TO STABLE");
            sb.AppendLine("You are switching from a beta version back to the latest stable release.");
            sb.AppendLine();
        }

        for (var i = 0; i < releases.Count; i++)
        {
            var r = releases[i];

            if (i > 0)
                sb.AppendLine().AppendLine("─────────────────────────────────").AppendLine();

            var label = r.IsPrerelease ? " [PRE-RELEASE]" : "";
            sb.AppendLine($"v{r.Version}{label}");
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
}
