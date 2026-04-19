using System.Text.Json;
using System.Text.Json.Serialization;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Storage.Dto;

namespace SmartCon.Core.Services.Storage;

/// <summary>
/// Pure-C# serializer for fitting mapping data (ADR-012). Used by
/// <c>RevitFittingMappingRepository</c> to store <see cref="MappingPayload"/> as a JSON
/// string inside the ExtensibleStorage DataStorage of a project, and by the
/// Settings window to Import/Export JSON files manually.
/// </summary>
/// <remarks>
/// This class is intentionally free of Revit API references so that serialization
/// round-trip can be covered by unit tests (I-09).
/// </remarks>
public static class FittingMappingJsonSerializer
{
    /// <summary>Current schema version for newly serialized payloads.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Serializes the supplied connector types and mapping rules to a JSON string
    /// tagged with <see cref="CurrentVersion"/>.
    /// </summary>
    public static string Serialize(
        IReadOnlyList<ConnectorTypeDefinition> types,
        IReadOnlyList<FittingMappingRule> rules)
    {
#if NETFRAMEWORK
        if (types is null) throw new ArgumentNullException(nameof(types));
        if (rules is null) throw new ArgumentNullException(nameof(rules));
#else
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(rules);
#endif

        var dto = new MappingPayloadDto
        {
            SchemaVersion = CurrentVersion,
            ConnectorTypes = types.Select(ToDto).ToList(),
            MappingRules = rules.Select(ToDto).ToList(),
        };
        return JsonSerializer.Serialize(dto, SerializerOptions);
    }

    /// <summary>
    /// Serializes a full <see cref="MappingPayload"/>. The output schema version is
    /// always overwritten with <see cref="CurrentVersion"/> so callers do not need to
    /// manage version bookkeeping manually.
    /// </summary>
    public static string Serialize(MappingPayload payload)
    {
#if NETFRAMEWORK
        if (payload is null) throw new ArgumentNullException(nameof(payload));
#else
        ArgumentNullException.ThrowIfNull(payload);
#endif
        return Serialize(payload.ConnectorTypes, payload.MappingRules);
    }

    /// <summary>
    /// Deserializes a JSON string produced by <see cref="Serialize(MappingPayload)"/>
    /// or a legacy AppData file (<c>connector-mapping.json</c>).
    /// </summary>
    /// <remarks>
    /// Returns <see cref="MappingPayload.Empty"/> for <c>null</c>, empty or whitespace input.
    /// Missing <c>schemaVersion</c> is normalized to <see cref="CurrentVersion"/>.
    /// </remarks>
    /// <exception cref="JsonException">The input is not valid JSON.</exception>
    public static MappingPayload Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return MappingPayload.Empty;

        var dto = JsonSerializer.Deserialize<MappingPayloadDto>(json!, SerializerOptions)
                  ?? new MappingPayloadDto();

        var version = dto.SchemaVersion > 0 ? dto.SchemaVersion : CurrentVersion;

        var types = (dto.ConnectorTypes ?? [])
            .Select(FromDto)
            .ToList();

        var rules = (dto.MappingRules ?? [])
            .Select(FromDto)
            .ToList();

        return new MappingPayload(version, types, rules);
    }

    /// <summary>
    /// Reads and deserializes a JSON file. Returns <c>null</c> when the file is missing
    /// or cannot be parsed (IO errors, invalid JSON). Callers should log the reason
    /// and surface a user-facing message.
    /// </summary>
    public static MappingPayload? TryReadFromFile(string path)
    {
#if NETFRAMEWORK
        if (path is null) throw new ArgumentNullException(nameof(path));
#else
        ArgumentNullException.ThrowIfNull(path);
#endif
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return Deserialize(json);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a payload to a JSON file. Creates the parent directory if it does not exist.
    /// </summary>
    public static void WriteToFile(string path, MappingPayload payload)
    {
#if NETFRAMEWORK
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (payload is null) throw new ArgumentNullException(nameof(payload));
#else
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(payload);
#endif

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, Serialize(payload));
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private static ConnectorTypeDto ToDto(ConnectorTypeDefinition t) => new()
    {
        Code = t.Code,
        Name = t.Name,
        Description = t.Description,
    };

    private static ConnectorTypeDefinition FromDto(ConnectorTypeDto d) => new()
    {
        Code = d.Code,
        Name = d.Name ?? string.Empty,
        Description = d.Description ?? string.Empty,
    };

    private static MappingRuleDto ToDto(FittingMappingRule r) => new()
    {
        FromType = r.FromType.Value,
        ToType = r.ToType.Value,
        IsDirectConnect = r.IsDirectConnect,
        FittingFamilies = r.FittingFamilies.Select(ToDto).ToList(),
        ReducerFamilies = r.ReducerFamilies.Select(ToDto).ToList(),
    };

    private static FittingMappingRule FromDto(MappingRuleDto d) => new()
    {
        FromType = new ConnectionTypeCode(d.FromType),
        ToType = new ConnectionTypeCode(d.ToType),
        IsDirectConnect = d.IsDirectConnect,
        FittingFamilies = (d.FittingFamilies ?? []).Select(FromDto).ToList(),
        ReducerFamilies = (d.ReducerFamilies ?? []).Select(FromDto).ToList(),
    };

    private static FittingMappingDto ToDto(FittingMapping f) => new()
    {
        FamilyName = f.FamilyName,
        SymbolName = f.SymbolName,
        Priority = f.Priority,
    };

    private static FittingMapping FromDto(FittingMappingDto d) => new()
    {
        FamilyName = d.FamilyName ?? string.Empty,
        SymbolName = d.SymbolName ?? "*",
        Priority = d.Priority,
    };
}
