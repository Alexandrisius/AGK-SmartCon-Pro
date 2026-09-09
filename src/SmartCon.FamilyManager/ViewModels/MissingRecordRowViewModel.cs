using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One selectable candidate row of the "Очистить недоступные записи" dialog
/// (Issue #133). Flat display projection of
/// <see cref="MissingRecordCandidate"/> plus a checkbox state.
/// </summary>
public sealed partial class MissingRecordRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public MissingRecordCandidate Candidate { get; }

    public string CatalogItemId => Candidate.CatalogItemId;
    public string ItemName => Candidate.ItemName;
    public string VersionLabel => Candidate.VersionLabel;
    public string FileName => Candidate.FileName;
    public string RelativePath => Candidate.RelativePath;
    public int RevitVersion => Candidate.RevitVersion;
    public string ReasonText { get; }

    public MissingRecordRowViewModel(MissingRecordCandidate candidate)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        ReasonText = candidate.Reason == MissingRecordReason.MarkedMissing
            ? LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ReasonMarked)
                ?? "Помечено при обновлении"
            : LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ReasonFileMissing)
                ?? "Файл отсутствует";
    }
}
