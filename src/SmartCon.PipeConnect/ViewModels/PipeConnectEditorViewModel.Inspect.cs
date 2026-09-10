using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Views;
using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    /// <summary>Inspect zoom radius: 0.75 m around the active connector (internal units, I-02).</summary>
    private const double InspectRadiusFeet = 750.0 * MmToFeet;

    /// <summary>Zoom ± scale factors: &lt;1 zooms in (smaller visible area), &gt;1 zooms out.</summary>
    private const double ZoomInFactor = 0.7;
    private const double ZoomOutFactor = 1.0 / 0.7;

    /// <summary>
    /// View-side accessor for the editor window bounds (set by PipeConnectEditorView).
    /// Null in unit tests — the zoom then centers on the whole view without occlusion compensation.
    /// </summary>
    public IEditorWindowBoundsAccessor? WindowBoundsAccessor { get; set; }

    /// <summary>
    /// "Просмотр" button: zooms the active graphical view to the ACTIVE dynamic connector
    /// (_activeDynamic changes when working with the chain), offset so the connector
    /// is not hidden behind this modal window. UIView zoom starts no transaction,
    /// so this is safe in the modal command context (I-01a).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void Inspect()
    {
        if (_activeDynamic is null) return;

        var result = _viewNavigation.ZoomToPoint(
            _activeDynamic.Origin, InspectRadiusFeet, WindowBoundsAccessor?.GetWindowBoundsInPixels());

        StatusMessage = ToInspectStatus(result);
    }

    /// <summary>Zoom in around the visible-zone center (no drift behind the dialog).</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void ZoomIn() => ZoomByFactorSilent(ZoomInFactor);

    /// <summary>Zoom out around the visible-zone center.</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void ZoomOut() => ZoomByFactorSilent(ZoomOutFactor);

    /// <summary>
    /// Zoom ± around the ACTIVE dynamic connector: re-centers on it AND scales the
    /// current zoom in one operation (no zoom-level reset, no double-zoom flicker).
    /// </summary>
    private void ZoomByFactorSilent(double factor)
    {
        if (_activeDynamic is null) return;

        var result = _viewNavigation.ZoomByFactorToPoint(
            factor, _activeDynamic.Origin, WindowBoundsAccessor?.GetWindowBoundsInPixels());
        if (result != ZoomToPointResult.Success)
            StatusMessage = ToInspectStatus(result);
    }

    private static string ToInspectStatus(ZoomToPointResult result) => result switch
    {
        ZoomToPointResult.Success => LocalizationService.GetString("Status_InspectDone"),
        ZoomToPointResult.NoActiveGraphicalView => LocalizationService.GetString("Status_InspectNoView"),
        ZoomToPointResult.UIViewNotFound => LocalizationService.GetString("Status_InspectNoView"),
        _ => LocalizationService.GetString("Status_InspectFailed"),
    };
}
