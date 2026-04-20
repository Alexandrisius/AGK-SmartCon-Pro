using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Service for checking, downloading, and staging plugin updates from GitHub Releases.
/// </summary>
public interface IUpdateService
{
    /// <summary>Check GitHub Releases for a newer version. Returns null if up-to-date.</summary>
    Task<UpdateInfo?> CheckForUpdateAsync();

    /// <summary>Download the update ZIP and return the local file path.</summary>
    Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null);

    /// <summary>Extract the downloaded ZIP to a staging directory.</summary>
    Task StageUpdateAsync(string zipPath);

    /// <summary>Check whether a staged update exists on disk.</summary>
    Task<PendingUpdate?> GetPendingUpdateAsync();

    /// <summary>Apply the staged update (called by the external updater process).</summary>
    Task ApplyPendingUpdateAsync();

    /// <summary>Current plugin version string.</summary>
    string GetCurrentVersion();

    /// <summary>Check for a multi-version pending update (targets multiple Revit versions).</summary>
    Task<MultiVersionPendingUpdate?> GetMultiVersionPendingUpdateAsync();
}
