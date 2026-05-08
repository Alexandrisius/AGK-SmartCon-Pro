using SmartCon.Core.Services.Interfaces;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Carries payload for a TreeView drag-and-drop operation.
/// </summary>
public sealed record TreeViewDropInfo(object? Payload, object? Target) : IDropInfo;
