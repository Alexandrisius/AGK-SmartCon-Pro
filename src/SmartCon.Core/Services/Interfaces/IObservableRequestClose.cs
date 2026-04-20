namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Exposes a close request event that views can observe to close themselves.
/// </summary>
public interface IObservableRequestClose
{
    /// <summary>
    /// Raised when the bound view should close.
    /// </summary>
    event Action? RequestClose;
}
