namespace SmartCon.Core.Models;

/// <summary>
/// Abstraction used by <see cref="Services.Interfaces.IDialogService.ShowFittingCtcSetup"/>
/// to expose connector setup data to the UI layer without Core depending on a concrete
/// WPF/INotifyPropertyChanged implementation. The UI layer supplies a binding-aware
/// implementation (see <c>SmartCon.PipeConnect.ViewModels.FittingCtcSetupItem</c>).
/// </summary>
public interface IFittingCtcSetupItem
{
    /// <summary>Connector index within the fitting family.</summary>
    int ConnectorIndex { get; }

    /// <summary>Name of the parameter driving the connector diameter (may be empty).</summary>
    string ParameterName { get; }

    /// <summary>Nominal connector diameter in millimetres (display purposes).</summary>
    double DiameterMm { get; }

    /// <summary>Pre-selected connector type (e.g. guessed from the mapping rule).</summary>
    ConnectorTypeDefinition? PreSelectedType { get; }

    /// <summary>User-selected connector type. Mutable: bound to the setup dialog.</summary>
    ConnectorTypeDefinition? SelectedType { get; set; }

    /// <summary>Human-readable display text for the connector.</summary>
    string DisplayText { get; }
}
