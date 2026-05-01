namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Defines which family parameters to extract for a given category.
/// Presets are stored per-category in the database. Child categories inherit parameters from parent categories.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="CategoryId">Revit category ID this preset applies to (null = global default).</param>
/// <param name="Parameters">Ordered list of parameters to extract.</param>
/// <param name="CreatedAtUtc">When the preset was created.</param>
/// <param name="UpdatedAtUtc">When the preset was last updated.</param>
public sealed record AttributePreset(
    string Id,
    string? CategoryId,
    IReadOnlyList<AttributePresetParameter> Parameters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
