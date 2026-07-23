using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

/// <summary>
/// Result dialog shown after a successful Share Project operation.
/// Displays the shared file path, purge statistics and elapsed time,
/// and lets the user open the destination folder in Explorer.
/// </summary>
public sealed partial class ShareResultViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private string _sharedFilePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _statisticsText = string.Empty;

    public event Action<bool?>? RequestClose;

    public ShareResultViewModel(string sharedFilePath, int deletedCount, double elapsedSeconds)
    {
        _sharedFilePath = sharedFilePath;
        _fileName = Path.GetFileName(sharedFilePath);

        var deletedLabel = LocalizationService.GetString("PM_ShareResult_Deleted") ?? "Elements deleted: {0}";
        var elapsedLabel = LocalizationService.GetString("PM_ShareResult_Elapsed") ?? "Time: {0}s";
        _statisticsText =
            string.Format(deletedLabel, deletedCount) +
            "   •   " +
            string.Format(elapsedLabel, elapsedSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            if (File.Exists(SharedFilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SharedFilePath}\"") { UseShellExecute = true });
            }
            else
            {
                var dir = Path.GetDirectoryName(SharedFilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"ShareResult.OpenFolder failed (ignored): {ex.Message}");
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(true);
}
