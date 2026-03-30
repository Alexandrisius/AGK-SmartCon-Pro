using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// ViewModel компактного модального окна выбора типа коннектора (S1.1).
/// </summary>
public sealed partial class MiniTypeSelectorViewModel : ObservableObject
{
    [ObservableProperty]
    private ConnectorTypeDefinition? _selectedType;

    public IReadOnlyList<ConnectorTypeDefinition> AvailableTypes { get; }

    /// <summary>
    /// Поднимается View, чтобы закрыть окно (RequestClose += Close).
    /// </summary>
    public event Action? RequestClose;

    public MiniTypeSelectorViewModel(IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        AvailableTypes = availableTypes;
    }

    [RelayCommand]
    private void Select()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        SelectedType = null;
        RequestClose?.Invoke();
    }
}
