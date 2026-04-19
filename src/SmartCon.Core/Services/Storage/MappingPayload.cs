using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Storage;

/// <summary>
/// Immutable snapshot of fitting mapping data for serialization.
/// Stored inside <c>ExtensibleStorage.DataStorage</c> per project (ADR-012)
/// and used as the payload for manual Import/Export JSON files.
/// </summary>
public sealed record MappingPayload(
    int SchemaVersion,
    IReadOnlyList<ConnectorTypeDefinition> ConnectorTypes,
    IReadOnlyList<FittingMappingRule> MappingRules)
{
    /// <summary>
    /// Empty payload tagged with <see cref="FittingMappingJsonSerializer.CurrentVersion"/>.
    /// Returned by <see cref="FittingMappingJsonSerializer.Deserialize"/> when the
    /// input is empty/null or when the caller requests a default value.
    /// </summary>
    public static MappingPayload Empty { get; } = new(
        FittingMappingJsonSerializer.CurrentVersion,
        Array.Empty<ConnectorTypeDefinition>(),
        Array.Empty<FittingMappingRule>());
}
