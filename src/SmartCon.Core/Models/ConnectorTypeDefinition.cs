namespace SmartCon.Core.Models;

/// <summary>
/// Определение типа коннектора для пользовательского справочника.
/// Хранится в JSON (AppData). Отображается в MiniTypeSelector и MappingEditor.
/// </summary>
public sealed record ConnectorTypeDefinition
{
    public required int Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}
