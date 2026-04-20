using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// WPF-bindable implementation of <see cref="IFittingCtcSetupItem"/>.
/// Represents one connector of a fitting family in the CTC (ConnectionTypeCode) setup dialog.
/// </summary>
public sealed partial class FittingCtcSetupItem : ObservableObject, IFittingCtcSetupItem
{
    /// <inheritdoc />
    public int ConnectorIndex { get; init; }

    /// <inheritdoc />
    public string ParameterName { get; init; } = string.Empty;

    /// <inheritdoc />
    public double DiameterMm { get; init; }

    /// <inheritdoc />
    public ConnectorTypeDefinition? PreSelectedType { get; init; }

    /// <inheritdoc />
    public string DisplayText => string.IsNullOrEmpty(ParameterName)
        ? $"Ø{DiameterMm:F0} мм"
        : $"{ParameterName} = Ø{DiameterMm:F0} мм";

    [ObservableProperty]
    private ConnectorTypeDefinition? _selectedType;
}
