using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdateAsync();
    Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null);
    Task StageUpdateAsync(string zipPath);
    Task<PendingUpdate?> GetPendingUpdateAsync();
    Task ApplyPendingUpdateAsync();
    string GetCurrentVersion();
}
