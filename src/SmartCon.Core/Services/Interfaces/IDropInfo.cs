namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Minimal contract describing a drop target context.
/// Implementations live in UI layer; Core only references the interface.
/// </summary>
public interface IDropInfo
{
    /// <summary>The data item being dragged.</summary>
    object? Payload { get; }
    
    /// <summary>The target item under the cursor, if any.</summary>
    object? Target { get; }
}
