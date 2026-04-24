using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.ViewModels;

[Obsolete("LEGACY: CTC now assigned automatically via Reflect button. Dialog no longer invoked.")]
public sealed partial class FittingCtcSetupViewModel : ObservableObject, IObservableRequestClose
{
    public string FamilyName { get; }
    public string SymbolName { get; }
    public ObservableCollection<FittingCtcSetupItem> Connectors { get; }
    public IReadOnlyList<ConnectorTypeDefinition> AvailableTypes { get; }

    [ObservableProperty]
    private bool _isValid;

    public event Action<bool?>? RequestClose;

    public FittingCtcSetupViewModel(
        string familyName,
        string symbolName,
        List<FittingCtcSetupItem> connectors,
        IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        FamilyName = familyName;
        SymbolName = symbolName;
        Connectors = new ObservableCollection<FittingCtcSetupItem>(connectors);
        AvailableTypes = availableTypes;

        foreach (var item in Connectors)
        {
            if (item.PreSelectedType is not null)
                item.SelectedType = item.PreSelectedType;
            item.PropertyChanged += (_, _) => UpdateIsValid();
        }
        UpdateIsValid();
    }

    private void UpdateIsValid()
    {
        IsValid = Connectors.All(c => c.SelectedType is not null);
    }

    [RelayCommand]
    private void Select()
    {
        if (!IsValid) return;
        RequestClose?.Invoke(null);
    }

    [RelayCommand]
    private void Cancel()
    {
        IsValid = false;
        RequestClose?.Invoke(null);
    }
}
