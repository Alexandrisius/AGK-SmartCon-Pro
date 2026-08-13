using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One follow-up action button of the status details dialog (e.g. "Open
/// validation report", "Check", "Update"). The dialog closes first, then
/// <see cref="Execute"/> runs — the action may start a long Revit
/// operation and must not keep the dialog modal on top of it.
/// </summary>
public sealed record StatusDetailsAction(string Label, Action Execute);

/// <summary>
/// Read-only details dialog behind the clickable status badges (#210):
/// family/node name, the list of its <see cref="StatusNotice"/> entries
/// (title + bullet list of names + short guidance), and optional follow-up
/// actions. Two separate views by design: the PROBLEM view (warning/error
/// notices + update/validation-report actions, opened by the warning
/// badges) and the RELATED-ELEMENTS view (info notices only, no actions,
/// opened by the paperclip / info badges). The type presence dot is NOT
/// here — it is a placement shortcut (PlaceTypeFromIndicator).
/// </summary>
public sealed partial class StatusDetailsViewModel : ObservableObject, IObservableRequestClose
{
    public event Action<bool?>? RequestClose;

    /// <summary>Display name of the family / tree node the notices belong to.</summary>
    public string Title { get; }

    /// <summary>Optional secondary line (e.g. category path). Hidden when null/empty.</summary>
    public string? Subtitle { get; }

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public IReadOnlyList<StatusNotice> Notices { get; }

    /// <summary>Worst severity across <see cref="Notices"/>; drives the header icon.</summary>
    public StatusNoticeSeverity WorstSeverity { get; }

    public IReadOnlyList<StatusDetailsAction> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    /// <summary>Exactly one action → rendered as a direct button.</summary>
    public bool HasSingleAction => Actions.Count == 1;

    /// <summary>The single action (null when <see cref="HasSingleAction"/> is false).</summary>
    public StatusDetailsAction? SingleAction => HasSingleAction ? Actions[0] : null;

    /// <summary>Two or more actions → rendered as a split button opening a dropdown menu.</summary>
    public bool HasActionMenu => Actions.Count > 1;

    /// <summary>
    /// Label of the split button opening the actions dropdown (e.g.
    /// «Обновить все типы»); meaningful only when <see cref="HasActionMenu"/>.
    /// </summary>
    public string? ActionsMenuLabel { get; }

    public StatusDetailsViewModel(
        string title,
        string? subtitle,
        IReadOnlyList<StatusNotice> notices,
        IReadOnlyList<StatusDetailsAction>? actions = null,
        string? actionsMenuLabel = null)
    {
        Title = title;
        Subtitle = subtitle;
        Notices = notices;
        Actions = actions ?? Array.Empty<StatusDetailsAction>();
        ActionsMenuLabel = actionsMenuLabel;
        WorstSeverity = notices.Count == 0
            ? StatusNoticeSeverity.Info
            : notices.Max(n => n.Severity);
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void RunAction(StatusDetailsAction? action)
    {
        if (action is null) return;
        RequestClose?.Invoke(null);
        action.Execute();
    }
}
