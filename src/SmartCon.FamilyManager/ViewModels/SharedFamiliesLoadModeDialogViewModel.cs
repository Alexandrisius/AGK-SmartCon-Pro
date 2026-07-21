using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Dialog asking the user how to load a single shared nested family
/// that conflicts with an existing one in the project.
/// Invoked from IFamilyLoadOptions.OnSharedFamilyFound via IFamilyManagerDialogService.
/// MUST run on Revit main thread (the WPF ShowDialog blocks the calling thread until user decides).
///
/// Closing via the window X button (Alt+F4) is treated as Skip — the user
/// did not explicitly choose a loading mode, so the callback signals Revit
/// to abort loading this shared nested family (and consequently the parent family).
/// There is no Cancel button or Escape binding by design — the user must
/// make an explicit choice via Apply.
/// </summary>
public sealed partial class SharedFamiliesLoadModeDialogViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _sharedFamilyName = string.Empty;
    [ObservableProperty] private string _inUseWarning = string.Empty;
    [ObservableProperty] private bool _isFamilyInUse;
    [ObservableProperty] private SharedFamiliesLoadChoice _selectedChoice = SharedFamiliesLoadChoice.UseProject;
    [ObservableProperty] private string _batchProgress = string.Empty;
    [ObservableProperty] private string _sourceIndicator = string.Empty;
    [ObservableProperty] private bool _showBatchProgress;
    [ObservableProperty] private bool _showSourceIndicator;

    public event Action<bool?>? RequestClose;

    public SharedFamiliesLoadChoice Result { get; private set; } = SharedFamiliesLoadChoice.UseProject;

    public bool IsUseProjectSelected
    {
        get => SelectedChoice == SharedFamiliesLoadChoice.UseProject;
        set
        {
            if (value) SelectedChoice = SharedFamiliesLoadChoice.UseProject;
            OnPropertyChanged(nameof(IsUseProjectSelected));
        }
    }

    public bool IsOverwriteParamsSelected
    {
        get => SelectedChoice == SharedFamiliesLoadChoice.OverwriteParameters;
        set
        {
            if (value) SelectedChoice = SharedFamiliesLoadChoice.OverwriteParameters;
            OnPropertyChanged(nameof(IsOverwriteParamsSelected));
        }
    }

    public bool IsOverwriteAllSelected
    {
        get => SelectedChoice == SharedFamiliesLoadChoice.OverwriteAll;
        set
        {
            if (value) SelectedChoice = SharedFamiliesLoadChoice.OverwriteAll;
            OnPropertyChanged(nameof(IsOverwriteAllSelected));
        }
    }

    partial void OnSelectedChoiceChanged(SharedFamiliesLoadChoice value)
    {
        OnPropertyChanged(nameof(IsUseProjectSelected));
        OnPropertyChanged(nameof(IsOverwriteParamsSelected));
        OnPropertyChanged(nameof(IsOverwriteAllSelected));
    }

    [RelayCommand]
    private void Apply()
    {
        Result = SelectedChoice;
        RequestClose?.Invoke(true);
    }

    public static SharedFamiliesLoadModeDialogViewModel Create(SharedFamilyDecisionRequest request)
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_LoadShared_DialogTitle)
                    ?? "Shared nested family — loading mode";
        var message = LanguageManager.Format(
            StringLocalization.Keys.FM_LoadShared_Message,
            request.SharedFamilyName);
        var inUseWarning = request.IsFamilyInUse
            ? (LanguageManager.GetString(StringLocalization.Keys.FM_LoadShared_InUseWarning) ?? "")
            : string.Empty;

        string batchProgress = string.Empty;
        bool showBatchProgress = false;
        if (request.TotalInBatch > 1)
        {
            // TotalInBatch = ALL shared nested in the .rfa (catalog), NOT the
            // conflict count — Revit fires OnSharedFamilyFound only for nested
            // that are both loaded in the project AND changed. The text must
            // not imply "N dialogs are coming" — only the ordinal is truthful.
            batchProgress = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_LoadShared_BatchProgress)
                    ?? "Conflict #{0} — file has {1} shared nested families, Revit asks only for changed ones",
                request.IndexInBatch, request.TotalInBatch);
            showBatchProgress = true;
        }

        string sourceIndicator = string.Empty;
        bool showSourceIndicator = false;
        switch (request.NameSource)
        {
            case SharedFamilyNameSource.CatalogDb:
                sourceIndicator = LanguageManager.GetString(StringLocalization.Keys.FM_LoadShared_SourceFromCatalog)
                    ?? "name from SmartCon catalog";
                showSourceIndicator = true;
                break;
            case SharedFamilyNameSource.FallbackPlaceholder:
                sourceIndicator = LanguageManager.GetString(StringLocalization.Keys.FM_LoadShared_SourcePlaceholder)
                    ?? "name unavailable — re-import the family in Family Manager to populate";
                showSourceIndicator = true;
                break;
            case SharedFamilyNameSource.RevitApi:
            default:
                break;
        }

        return new SharedFamiliesLoadModeDialogViewModel
        {
            Title = title,
            Message = message,
            SharedFamilyName = request.SharedFamilyName,
            IsFamilyInUse = request.IsFamilyInUse,
            InUseWarning = inUseWarning,
            BatchProgress = batchProgress,
            ShowBatchProgress = showBatchProgress,
            SourceIndicator = sourceIndicator,
            ShowSourceIndicator = showSourceIndicator
        };
    }
}
