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

public sealed partial class GitHubUpdateService : IUpdateService
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

    private static readonly int[] s_supportedRevitVersions = [2019, 2020, 2021, 2022, 2023, 2024, 2025, 2026, 2027];

    private const int MaxRetries = 3;
    private const int TransientFaultDelayMs = 500;

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

#if NET48
        // On .NET Framework 4.8 the only way to influence HttpClient's socket layer is via
        // ServicePointManager (HttpClient → HttpWebRequest → WinHTTP). Setting ReusePort = true
        // enables the native SO_REUSE_UNICASTPORT socket option, which **defers source-port
        // allocation** until ConnectEx and chooses the port with the 4-tuple in mind. This
        // makes the connection succeed even when the kernel's ephemeral port range has been
        // partially blocked by Hyper-V / winnat / Docker exclusions, which would otherwise
        // surface as WSAEACCES (10013) on a random subset of attempts.
        //
        // On .NET 5+ this property is a no-op and SocketsHttpHandler enables ReuseUnicastPort
        // by default, so we do not need to do anything for R25+.
        //
        // See: https://learn.microsoft.com/dotnet/api/system.net.servicepointmanager.reuseport
        //      https://stackoverflow.com/q/44548444
        try
        {
            System.Net.ServicePointManager.ReusePort = true;
        }
        catch
        {
            // Best-effort: never let a static property tweak break update checks.
        }
#endif
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
            "R27" => Path.Combine(s_smartConDir, "2027"),
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
        if (installed.Contains(2027))
            tags.Add("R27");

        if (tags.Count == 0)
            tags = ["R19", "R21", "R24", "R25", "R26", "R27"];

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
        if (!SemVersion.TryParse(remote, out var r)) return false;
        if (!SemVersion.TryParse(current, out var c)) return false;
        return r > c;
    }

    private record AssetInfo(string Name, string DownloadUrl, long Size);
}
