using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IAttributeDefinitionRepository
{
    Task<IReadOnlyList<AttributeDefinition>> GetAllAsync(CancellationToken ct = default);
    Task<AttributeDefinition?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<AttributeDefinition?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<AttributeDefinition> CreateAsync(string name, string? group, CancellationToken ct = default);
    Task<AttributeDefinition> UpdateAsync(string id, string? name, string? group, bool? isActive, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, string? excludeId, CancellationToken ct = default);
}
