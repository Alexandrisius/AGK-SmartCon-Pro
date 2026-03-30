using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Элемент списка семейств с приоритетом в FamilySelector.
/// Приоритет = порядок в списке (1, 2, 3...).
/// </summary>
public sealed partial class FamilyPriorityItem : ObservableObject
{
    [ObservableProperty]
    private string _familyName = string.Empty;

    [ObservableProperty]
    private string _symbolName = "*";

    [ObservableProperty]
    private int _priority;

    /// <summary>Для UI: "1. СварнойШов"</summary>
    public string DisplayText => $"{Priority}. {FamilyName}";

    public static FamilyPriorityItem From(FittingMapping mapping, int priority) =>
        new() { FamilyName = mapping.FamilyName, SymbolName = mapping.SymbolName, Priority = priority };

    public FittingMapping ToMapping() =>
        new() { FamilyName = FamilyName, SymbolName = SymbolName, Priority = Priority };
}

/// <summary>
/// ViewModel окна выбора семейств фитингов с приоритетами.
/// Открывается из MappingEditor для редактирования списка семейств правила.
/// </summary>
public sealed partial class FamilySelectorViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _availableFamilyNames;

    [ObservableProperty]
    private ObservableCollection<FamilyPriorityItem> _families = [];

    [ObservableProperty]
    private FamilyPriorityItem? _selectedFamily;

    [ObservableProperty]
    private string _selectedFamilyToAdd = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Поднимается View для закрытия окна с результатом.</summary>
    public event Action<bool>? RequestClose;

    public FamilySelectorViewModel(
        IEnumerable<FittingMapping> existingFamilies,
        IReadOnlyList<string> availableFamilyNames)
    {
        _availableFamilyNames = availableFamilyNames;

        var priority = 1;
        foreach (var f in existingFamilies)
        {
            Families.Add(FamilyPriorityItem.From(f, priority++));
        }
    }

    public IEnumerable<FittingMapping> GetResult() =>
        Families.Select(f => f.ToMapping());

    [RelayCommand]
    private void AddFamily()
    {
        if (string.IsNullOrWhiteSpace(SelectedFamilyToAdd))
        {
            StatusMessage = "Выберите семейство для добавления";
            return;
        }

        if (Families.Any(f => f.FamilyName == SelectedFamilyToAdd))
        {
            StatusMessage = "Это семейство уже в списке";
            return;
        }

        var newItem = new FamilyPriorityItem
        {
            FamilyName = SelectedFamilyToAdd,
            SymbolName = "*",
            Priority = Families.Count + 1
        };

        Families.Add(newItem);
        SelectedFamily = newItem;
        SelectedFamilyToAdd = string.Empty;
        StatusMessage = $"Добавлено: {newItem.FamilyName}";
    }

    [RelayCommand]
    private void RemoveFamily()
    {
        if (SelectedFamily is null) return;

        var removedIndex = Families.IndexOf(SelectedFamily);
        Families.Remove(SelectedFamily);

        // Пересчитываем приоритеты
        for (int i = 0; i < Families.Count; i++)
        {
            Families[i].Priority = i + 1;
        }

        StatusMessage = $"Удалено. Осталось: {Families.Count}";

        // Выбираем следующий или предыдущий
        if (Families.Count > 0)
        {
            SelectedFamily = removedIndex < Families.Count
                ? Families[removedIndex]
                : Families[^1];
        }
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedFamily is null) return;

        var index = Families.IndexOf(SelectedFamily);
        if (index <= 0) return;

        // Меняем местами
        (Families[index], Families[index - 1]) = (Families[index - 1], Families[index]);

        // Пересчитываем приоритеты
        Families[index].Priority = index + 1;
        Families[index - 1].Priority = index;
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedFamily is null) return;

        var index = Families.IndexOf(SelectedFamily);
        if (index >= Families.Count - 1) return;

        // Меняем местами
        (Families[index], Families[index + 1]) = (Families[index + 1], Families[index]);

        // Пересчитываем приоритеты
        Families[index].Priority = index + 1;
        Families[index + 1].Priority = index + 2;
    }

    [RelayCommand]
    private void Confirm()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    public IReadOnlyList<string> AvailableFamilyNames => _availableFamilyNames;
}
