using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartCon.Core.Models;

/// <summary>
/// UI model for the CTC (ConnectionTypeCode) assignment dialog.
/// Each item represents one connector of a fitting family,
/// allowing the user to assign a connector type.
/// </summary>
public sealed class FittingCtcSetupItem : INotifyPropertyChanged
{
    public int ConnectorIndex { get; init; }
    public string ParameterName { get; init; } = string.Empty;
    public double DiameterMm { get; init; }
    public ConnectorTypeDefinition? PreSelectedType { get; init; }

    public string DisplayText => string.IsNullOrEmpty(ParameterName)
        ? $"Ø{DiameterMm:F0} мм"
        : $"{ParameterName} = Ø{DiameterMm:F0} мм";

    private ConnectorTypeDefinition? _selectedType;
    public ConnectorTypeDefinition? SelectedType
    {
        get => _selectedType;
        set { _selectedType = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
