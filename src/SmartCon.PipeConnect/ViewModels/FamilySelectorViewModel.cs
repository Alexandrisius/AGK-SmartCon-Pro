using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// ViewModel модального окна выбора семейств фитингов для правила маппинга (3C).
/// Два списка: доступные (отсортированы A→Z) и выбранные (порядок = приоритет).
/// </summary>
public sealed partial class FamilySelectorViewModel : ObservableObject
{
    public ObservableCollection<string> AvailableFamilies { get; }
    public ObservableCollection<string> SelectedFamilies { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string? _selectedAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private string? _selectedMapping;

    public bool Confirmed { get; private set; }

    /// <summary>Поднимается ViewModel для закрытия окна (View подписывается: RequestClose += Close).</summary>
    public event Action? RequestClose;

    public FamilySelectorViewModel(
        IReadOnlyList<string> availableFamilyNames,
        IReadOnlyList<FittingMapping> currentSelection)
    {
        var selectedNames = new HashSet<string>(currentSelection.Select(f => f.FamilyName),
            StringComparer.OrdinalIgnoreCase);

        AvailableFamilies = new ObservableCollection<string>(
            availableFamilyNames
                .Where(n => !selectedNames.Contains(n))
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));

        foreach (var f in currentSelection.OrderBy(f => f.Priority))
            SelectedFamilies.Add(f.FamilyName);
    }

    /// <summary>
    /// Результат диалога: упорядоченный список FittingMapping (Priority = позиция+1),
    /// или null если диалог отменён.
    /// </summary>
    public IReadOnlyList<FittingMapping>? GetResult() =>
        Confirmed
            ? SelectedFamilies
                .Select((name, i) => new FittingMapping { FamilyName = name, Priority = i + 1 })
                .ToList()
            : null;

    // ── Команды ────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (SelectedAvailable is null) return;
        var name = SelectedAvailable;
        SelectedFamilies.Add(name);
        AvailableFamilies.Remove(name);
        SelectedAvailable = null;
    }

    private bool CanAdd() => SelectedAvailable is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedMapping is null) return;
        var name = SelectedMapping;
        SelectedFamilies.Remove(name);
        SelectedMapping = null;

        // Добавить обратно в Available в отсортированном порядке
        var insertAt = FindSortedInsertIndex(AvailableFamilies, name);
        AvailableFamilies.Insert(insertAt, name);
    }

    private bool CanRemove() => SelectedMapping is not null;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedMapping is null) return;
        var idx = SelectedFamilies.IndexOf(SelectedMapping);
        if (idx <= 0) return;
        SelectedFamilies.Move(idx, idx - 1);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private bool CanMoveUp() =>
        SelectedMapping is not null && SelectedFamilies.IndexOf(SelectedMapping) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedMapping is null) return;
        var idx = SelectedFamilies.IndexOf(SelectedMapping);
        if (idx < 0 || idx >= SelectedFamilies.Count - 1) return;
        SelectedFamilies.Move(idx, idx + 1);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private bool CanMoveDown() =>
        SelectedMapping is not null &&
        SelectedFamilies.IndexOf(SelectedMapping) < SelectedFamilies.Count - 1;

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        RequestClose?.Invoke();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int FindSortedInsertIndex(ObservableCollection<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (StringComparer.CurrentCultureIgnoreCase.Compare(list[i], value) > 0)
                return i;
        return list.Count;
    }
}
