using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ICategoryRepository
{
    Task<IReadOnlyList<CategoryNode>> GetAllAsync(CancellationToken ct = default);
    Task<CategoryNode?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<CategoryNode> AddAsync(string name, string? parentId, int sortOrder, CancellationToken ct = default);
    Task<CategoryNode?> RenameAsync(string id, string newName, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<CategoryNode?> MoveAsync(string id, string? newParentId, int sortOrder, CancellationToken ct = default);
    Task<int> GetFamilyCountAsync(string categoryId, CancellationToken ct = default);
    Task ReorderAsync(IReadOnlyList<(string Id, int SortOrder)> items, CancellationToken ct = default);
    Task ReplaceAllAsync(IReadOnlyList<CategoryNode> categories, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetAllFamilyCountsAsync(CancellationToken ct = default);
}
