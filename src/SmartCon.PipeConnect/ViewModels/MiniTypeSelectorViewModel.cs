using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class MiniTypeSelectorViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private ConnectorTypeDefinition? _selectedType;

    public IReadOnlyList<ConnectorTypeDefinition> AvailableTypes { get; }

    public event Action<bool?>? RequestClose;

    public MiniTypeSelectorViewModel(IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        AvailableTypes = availableTypes;
    }

    [RelayCommand]
    private void Select()
    {
        RequestClose?.Invoke(null);
    }

    [RelayCommand]
    private void Cancel()
    {
        SelectedType = null;
        RequestClose?.Invoke(null);
    }
}
