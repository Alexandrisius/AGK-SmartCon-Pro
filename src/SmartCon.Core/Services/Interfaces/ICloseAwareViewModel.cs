namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Arguments passed to the ViewModel when the user initiates window close.
/// </summary>
public sealed class CloseConfirmationArgs
{
    /// <summary>
    /// Set to <c>true</c> to cancel the close operation.
    /// </summary>
    public bool Cancel { get; set; }

    /// <summary>
    /// Optional dialog result to set when the close proceeds.
    /// If <c>null</c>, the view uses its default (typically <c>false</c>).
    /// </summary>
    public bool? DialogResult { get; set; }

    /// <summary>
    /// An action to execute asynchronously after the Closing event has been processed.
    /// Use this when the ViewModel needs to trigger a command (e.g. Save or Cancel)
    /// that itself requests close, which would otherwise recurse into the Closing handler.
    /// </summary>
    public Action? DeferredAction { get; set; }
}

/// <summary>
/// Implemented by ViewModels that want to intercept or confirm user-initiated window close.
/// </summary>
public interface ICloseAwareViewModel
{
    /// <summary>
    /// Called synchronously when the user closes the window via X button or Alt+F4.
    /// </summary>
    void ConfirmClose(CloseConfirmationArgs args);
}
