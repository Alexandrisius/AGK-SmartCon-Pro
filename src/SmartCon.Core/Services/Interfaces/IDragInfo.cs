namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Minimal contract describing a drag operation payload.
/// Implementations live in UI layer; Core only references the interface.
/// </summary>
public interface IDragInfo
{
    /// <summary>The data item being dragged.</summary>
    object? Payload { get; }
    
    /// <summary>Optional display text for drag preview.</summary>
    string? DisplayText { get; }
}
