namespace SmartCon.Core.Models;

/// <summary>
/// Strongly typed wrapper over the connection type code
/// from the Connector.Description field (ADR-002).
/// Value = 0 means "undefined".
/// </summary>
public readonly record struct ConnectionTypeCode(int Value)
{
    public static readonly ConnectionTypeCode Undefined = new(0);

    public bool IsDefined => Value != 0;

    public override string ToString() => Value.ToString();

    /// <summary>
    /// Parses a string in the format "CODE" or "CODE.NAME.DESCRIPTION".
    /// Takes only the first segment before the dot.
    /// </summary>
    public static ConnectionTypeCode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Undefined;
        var segment = raw!.Split('.')[0].Trim();
        return int.TryParse(segment, out var v) && v != 0 ? new(v) : Undefined;
    }
}
