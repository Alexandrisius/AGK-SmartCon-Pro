using SmartCon.Core.Services.Interfaces;

namespace SmartCon.UI.Behaviors;

public sealed record TreeViewDragInfo(object? Payload, string? DisplayText) : IDragInfo;
