namespace SmartCon.Core.Models;

/// <summary>
/// Parsed representation of a connector description string in the format
/// <c>"{Code}.{Name}.{Description}"</c> (ADR-002 + bug #64 fix).
/// Each segment is stored after trimming leading/trailing whitespace.
/// <para>
/// Why a separate record from <see cref="ConnectionTypeCode"/>:
/// <see cref="ConnectionTypeCode"/> is a 4-byte value type used in 40+ hot-path
/// comparisons (CtcGuesser, FittingMapper, FittingInsertService) where only the
/// numeric <c>code</c> matters. This record carries the full triple used by
/// <c>IsKnownTypeDefinition</c> to verify the mapping against an actual
/// Revit-stored <c>ALL_MODEL_DESCRIPTION</c> / <c>connector.Description</c>.
/// </para>
/// </summary>
public sealed record ConnectorDescription(
    ConnectionTypeCode Code,
    string Name,
    string Description)
{
    /// <summary>Sentinel for an undefined/missing connection type (code=0).</summary>
    public static readonly ConnectorDescription Undefined = new(ConnectionTypeCode.Undefined, string.Empty, string.Empty);

    /// <summary>True when the code is non-zero (named/described type exists).</summary>
    public bool IsDefined => Code.IsDefined;

    public override string ToString()
        => Description.Length == 0
            ? (Name.Length == 0 ? Code.ToString() : $"{Code}.{Name}")
            : $"{Code}.{Name}.{Description}";

    /// <summary>
    /// Parses a string in the format <c>CODE</c>, <c>CODE.NAME</c>, or
    /// <c>CODE.NAME.DESCRIPTION</c>. The third segment is preserved as-is
    /// (it may contain dots — e.g. "ГОСТ 6357-81 §4.2.5").
    /// </summary>
    /// <remarks>
    /// Behaviour matches the legacy <see cref="ConnectionTypeCode.Parse"/>:
    /// empty/whitespace → <see cref="Undefined"/>; non-integer first segment
    /// or <c>0</c> → <see cref="Undefined"/>.
    /// </remarks>
    public static ConnectorDescription Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Undefined;
        }

        var segments = raw!.Split(new[] { '.' }, 3);
        var codeSegment = segments[0].Trim();
        var nameSegment = segments.Length > 1 ? segments[1].Trim() : string.Empty;
        var descSegment = segments.Length > 2 ? segments[2].Trim() : string.Empty;

        var ok = int.TryParse(codeSegment, out var v);
        if (!ok || v == 0)
        {
            return Undefined;
        }

        return new ConnectorDescription(new ConnectionTypeCode(v), nameSegment, descSegment);
    }

    /// <summary>
    /// Builds a description from a <see cref="ConnectorProxy"/>. Returns
    /// <see cref="Undefined"/> when the proxy has no parseable code.
    /// </summary>
    public static ConnectorDescription FromProxy(ConnectorProxy proxy)
        => Parse(BuildRawFromProxy(proxy));

    private static string BuildRawFromProxy(ConnectorProxy proxy)
    {
        if (!proxy.ConnectionTypeCode.IsDefined) return string.Empty;
        var name = proxy.ConnectionName ?? string.Empty;
        var desc = proxy.ConnectionDescription ?? string.Empty;
        if (desc.Length == 0 && name.Length == 0) return proxy.ConnectionTypeCode.ToString();
        if (desc.Length == 0) return $"{proxy.ConnectionTypeCode.Value}.{name}";
        return $"{proxy.ConnectionTypeCode.Value}.{name}.{desc}";
    }

    /// <summary>
    /// Equality for <c>IsKnownTypeDefinition</c> comparison: trims and uses
    /// ordinal case-insensitive comparison. Empty vs non-empty is NOT equal
    /// (strict, by design — see issue #64).
    /// </summary>
    public bool Matches(ConnectorTypeDefinition definition)
        => Code.Value == definition.Code
           && EqualsIgnoreCaseTrim(Name, definition.Name)
           && EqualsIgnoreCaseTrim(Description, definition.Description);

    private static bool EqualsIgnoreCaseTrim(string a, string b)
        => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(),
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Verifies whether a parsed connector description (code+name+description) matches
    /// any of the configured types in the mapping (issue #64).
    /// Comparison is ordinal, case-insensitive, with leading/trailing whitespace
    /// trimmed on both sides. Empty vs non-empty is treated as NOT equal (strict).
    /// </summary>
    /// <returns>
    /// <c>true</c> only if at least one mapping entry has the same
    /// <c>code</c>, <c>name</c>, and <c>description</c> as the parsed description.
    /// </returns>
    public static bool IsKnownTypeDefinition(
        ConnectorDescription parsed,
        IReadOnlyList<ConnectorTypeDefinition> mapping)
    {
        if (!parsed.IsDefined) return false;
        return mapping.Any(t => parsed.Matches(t));
    }
}