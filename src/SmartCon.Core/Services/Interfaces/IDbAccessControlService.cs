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
    /// <summary>
    /// Role-only "may write" (Owner/BimMaster, not banned) WITHOUT the
    /// plugin-compat gate (ADR-058). Drives UI that should be visible to
    /// editors only — e.g. the compat banner, which is meaningless for
    /// read-only roles (they are already gated by their role).
    /// </summary>
    bool IsEditorRole { get; }
    bool IsOwner { get; }
    bool IsBanned { get; }
    Task RefreshCurrentUserAsync(CancellationToken ct = default);
    void InvalidateCache();
}
