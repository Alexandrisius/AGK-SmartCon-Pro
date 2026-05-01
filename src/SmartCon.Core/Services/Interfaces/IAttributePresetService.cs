using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Manages attribute presets that define which parameters to extract for each category.
/// Presets support category inheritance: child categories inherit from parent categories.
/// </summary>
public interface IAttributePresetService
{
    /// <summary>Get all presets.</summary>
    Task<IReadOnlyList<AttributePreset>> GetAllPresetsAsync(CancellationToken ct = default);

    /// <summary>Get the preset directly assigned to a category (null if none).</summary>
    Task<AttributePreset?> GetPresetForCategoryAsync(string? categoryId, CancellationToken ct = default);

    /// <summary>Get effective parameters for a category, walking up the category hierarchy if needed.</summary>
    Task<IReadOnlyList<AttributePresetParameter>> GetEffectiveParametersAsync(string? categoryId, CancellationToken ct = default);

    /// <summary>Create a new preset for a category.</summary>
    Task<AttributePreset> CreatePresetAsync(string? categoryId, IReadOnlyList<AttributePresetParameter> parameters, CancellationToken ct = default);

    /// <summary>Update parameters of an existing preset.</summary>
    Task UpdatePresetAsync(string presetId, IReadOnlyList<AttributePresetParameter> parameters, CancellationToken ct = default);

    /// <summary>Delete a preset.</summary>
    Task DeletePresetAsync(string presetId, CancellationToken ct = default);
}
