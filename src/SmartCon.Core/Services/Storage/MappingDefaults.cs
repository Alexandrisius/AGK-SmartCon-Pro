using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Storage;

/// <summary>
/// Out-of-the-box fitting mapping: a single connector type "Стандартный" (code 1)
/// with a direct-connect rule and no fitting families.
/// </summary>
/// <remarks>
/// The defaults are virtual: <c>RevitFittingMappingRepository</c> returns them while
/// the project has no saved mapping, and PipeConnect connects elements directly
/// (undefined CTC == undefined CTC) without writing anything into the document.
/// The type name is language-stable because it is written into the pipe type
/// "Описание" parameter ("1.Стандартный") whenever a user explicitly assigns it.
/// </remarks>
public static class MappingDefaults
{
    public const int StandardTypeCode = 1;

    public const string StandardTypeName = "Стандартный";

    public static readonly ConnectorTypeDefinition StandardConnectorType = new()
    {
        Code = StandardTypeCode,
        Name = StandardTypeName,
        Description = string.Empty,
    };

    /// <summary>
    /// Direct-connect rule 1↔1 without any fitting or reducer families:
    /// nothing is inserted for the standard type.
    /// </summary>
    public static readonly FittingMappingRule StandardDirectRule = new()
    {
        FromType = new ConnectionTypeCode(StandardTypeCode),
        ToType = new ConnectionTypeCode(StandardTypeCode),
        IsDirectConnect = true,
        FittingFamilies = Array.Empty<FittingMapping>(),
        ReducerFamilies = Array.Empty<FittingMapping>(),
    };

    /// <summary>Fresh immutable payload with the standard type and direct rule.</summary>
    public static MappingPayload CreatePayload() => new(
        FittingMappingJsonSerializer.CurrentVersion,
        new[] { StandardConnectorType },
        new[] { StandardDirectRule });

    /// <summary>
    /// True when the mapping carries no user setup that could change fitting selection:
    /// at most the single standard type and no fitting/reducer families in any rule.
    /// In that state PipeConnect must not prompt for a connector type nor write CTC
    /// into pipe types or families — the connection is direct by definition.
    /// </summary>
    public static bool IsUnconfigured(
        IReadOnlyList<ConnectorTypeDefinition> types,
        IReadOnlyList<FittingMappingRule> rules)
    {
        if (types.Count > 1) return false;
        if (types.Count == 1 && !IsStandardType(types[0])) return false;

        foreach (var rule in rules)
        {
            if (rule.FittingFamilies.Count > 0 || rule.ReducerFamilies.Count > 0)
                return false;
        }

        return true;
    }

    private static bool IsStandardType(ConnectorTypeDefinition type)
        => type.Code == StandardTypeCode
           && string.Equals(type.Name.Trim(), StandardTypeName, StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrWhiteSpace(type.Description);
}