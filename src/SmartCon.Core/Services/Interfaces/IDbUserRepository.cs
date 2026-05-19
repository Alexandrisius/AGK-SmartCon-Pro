using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IDbUserRepository
{
    Task<DbUser?> GetUserAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<DbUser>> GetAllUsersAsync(CancellationToken ct = default);
    Task<DbUser> GetOrCreateUserAsync(UserIdentity identity, CancellationToken ct = default);
    Task<bool> UpdateUserRoleAsync(string userId, DbUserRole role, CancellationToken ct = default);
    Task<bool> UpdateUserStatusAsync(string userId, DbUserStatus status, CancellationToken ct = default);
    Task<bool> RemoveUserAsync(string userId, CancellationToken ct = default);
    Task<int> GetUserCountAsync(CancellationToken ct = default);
    Task<bool> TransferOwnershipAsync(string currentOwnerUserId, string newOwnerUserId, CancellationToken ct = default);
    Task<string?> GetOwnerIdentityAsync(CancellationToken ct = default);
}
