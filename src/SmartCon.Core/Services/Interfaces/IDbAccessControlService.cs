using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IDbAccessControlService
{
    Task<DbUserRole> GetCurrentUserRoleAsync(CancellationToken ct = default);
    Task<DbUser> GetCurrentUserAsync(CancellationToken ct = default);
    bool CanImport { get; }
    bool CanEdit { get; }
    bool CanManageUsers { get; }
    bool CanLoadToProject { get; }
    bool IsOwner { get; }
    bool IsBanned { get; }
    Task RefreshCurrentUserAsync(CancellationToken ct = default);
    void InvalidateCache();
}
