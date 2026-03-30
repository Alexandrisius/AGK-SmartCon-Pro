namespace SmartCon.Core.Models;

/// <summary>
/// Строго типизированная обёртка над кодом типа соединения
/// из поля Connector.Description (ADR-002).
/// Value = 0 означает "не определён".
/// </summary>
public readonly record struct ConnectionTypeCode(int Value)
{
    public static readonly ConnectionTypeCode Undefined = new(0);

    public bool IsDefined => Value != 0;

    public override string ToString() => Value.ToString();

    /// <summary>
    /// Разбирает строку вида "КОД" или "КОД.НАЗВАНИЕ.ОПИСАНИЕ".
    /// Берёт только первый сегмент до точки.
    /// </summary>
    public static ConnectionTypeCode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Undefined;
        var segment = raw.Split('.')[0].Trim();
        return int.TryParse(segment, out var v) && v != 0 ? new(v) : Undefined;
    }
}
