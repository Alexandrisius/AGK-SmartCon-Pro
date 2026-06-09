using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// File-system implementation of <see cref="IFamilySidecarLocator"/>.
/// Revit stores the Type Catalog (.txt) in the same folder as the .rfa
/// with the same basename. The lookup is case-insensitive to tolerate
/// unusual casing from upstream tools and Linux/Win file sharing.
/// </summary>
internal sealed class LocalFamilySidecarLocator : IFamilySidecarLocator
{
    private const int CopyMaxRetries = 3;
    private static readonly int[] CopyRetryDelaysMs = [100, 300, 900];

    public string? FindSidecarPath(string? rfaPath)
    {
        using var _scope = SmartConLogger.BeginScope("Sidecar",
            ("Method", "FindSidecarPath"));
        if (string.IsNullOrWhiteSpace(rfaPath))
        {
            SmartConLogger.Debug("FindSidecarPath: empty rfaPath → null");
            return null;
        }

        try
        {
            if (!File.Exists(rfaPath))
            {
                SmartConLogger.Debug($"FindSidecarPath: rfaPath does not exist '{rfaPath}' → null");
                return null;
            }

            var dir = Path.GetDirectoryName(rfaPath);
            // Use SafeFileName.GetBaseName (NOT Path.GetFileNameWithoutExtension) to
            // preserve internal dots used as type separators — e.g.
            // "BP_A0307_ITAP_ART.162_Амер угловая.rfa" → "BP_A0307_ITAP_ART.162_Амер угловая"
            // (Path.GetFileNameWithoutExtension would truncate to "BP_A0307_ITAP_ART"
            // and silently fail to find the matching sidecar).
            var nameNoExt = SafeFileName.GetBaseName(rfaPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(nameNoExt))
            {
                SmartConLogger.Debug($"FindSidecarPath: cannot derive dir/basename from '{rfaPath}' → null");
                return null;
            }

            // 1. Exact-name match (fast path, case-insensitive on Windows / Mac HFS+)
            var direct = Path.Combine(dir, nameNoExt + ".txt");
            if (File.Exists(direct))
            {
                SmartConLogger.Debug($"Found (direct): {direct}");
                return direct;
            }

            // 2. Enumerate directory for any *.txt that matches the basename
            //    case-insensitively. Tolerates cross-platform file systems.
            foreach (var candidate in Directory.EnumerateFiles(dir, "*.txt", SearchOption.TopDirectoryOnly))
            {
                var candidateName = SafeFileName.GetBaseName(candidate);
                if (string.Equals(candidateName, nameNoExt, StringComparison.OrdinalIgnoreCase))
                {
                    SmartConLogger.Debug($"Found (case-insensitive): {candidate}");
                    return candidate;
                }
            }

            SmartConLogger.Debug($"No .txt sidecar found for '{rfaPath}' (basename='{nameNoExt}', dir='{dir}')");
            return null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"FindSidecarPath failed for '{rfaPath}': {ex.Message}");
            return null;
        }
    }

    public async Task<string?> CopySidecarAsync(string sourceTxtPath, string destDir, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("Sidecar",
            ("Method", "CopySidecarAsync"));
        if (string.IsNullOrWhiteSpace(sourceTxtPath) || string.IsNullOrWhiteSpace(destDir))
        {
            SmartConLogger.Warn($"CopySidecarAsync: invalid arguments (source='{sourceTxtPath}', destDir='{destDir}')");
            return null;
        }

        if (!File.Exists(sourceTxtPath))
        {
            SmartConLogger.Warn($"CopySidecarAsync: source does not exist '{sourceTxtPath}'");
            return null;
        }

        try
        {
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(sourceTxtPath));

            for (var attempt = 0; attempt < CopyMaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await Task.Run(() =>
                    {
                        using var sourceStream = new FileStream(
                            sourceTxtPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite);
                        using var destStream = new FileStream(
                            destPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None);
                        sourceStream.CopyTo(destStream);
                        destStream.Flush();
                    }, ct);

                    SmartConLogger.Debug($"Copied '{sourceTxtPath}' → '{destPath}' (attempt {attempt + 1})");
                    return destPath;
                }
                catch (IOException) when (attempt < CopyMaxRetries - 1)
                {
                    var delay = CopyRetryDelaysMs[attempt];
                    SmartConLogger.Debug($"Copy attempt {attempt + 1} failed (IO), retrying in {delay}ms");
                    await Task.Delay(delay, ct);
                }
            }

            SmartConLogger.Warn($"CopySidecarAsync: failed to copy '{sourceTxtPath}' after {CopyMaxRetries} attempts");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"CopySidecarAsync unexpected error: {ex.Message}");
            return null;
        }
    }
}

