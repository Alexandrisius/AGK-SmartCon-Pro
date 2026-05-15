using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

public sealed class DbAccessControlService : IDbAccessControlService
{
    private readonly IDbUserRepository _userRepo;
    private readonly IUserIdentityService _identityService;
    private volatile DbUser? _cachedUser;

    public DbAccessControlService(IDbUserRepository userRepo, IUserIdentityService identityService)
    {
        _userRepo = userRepo;
        _identityService = identityService;
    }

    public bool CanImport => !IsBanned && (_cachedUser?.Role is DbUserRole.Owner or DbUserRole.BimMaster);
    public bool CanEdit => !IsBanned && (_cachedUser?.Role is DbUserRole.Owner or DbUserRole.BimMaster);
    public bool CanManageUsers => !IsBanned && (_cachedUser?.Role == DbUserRole.Owner);
    public bool CanLoadToProject => !IsBanned;
    public bool IsOwner => _cachedUser?.Role == DbUserRole.Owner;
    public bool IsBanned => _cachedUser?.Status == DbUserStatus.Banned;

    public async Task<DbUserRole> GetCurrentUserRoleAsync(CancellationToken ct = default)
    {
        if (_cachedUser is null)
            await RefreshCurrentUserAsync(ct);
        return _cachedUser?.Role ?? DbUserRole.Engineer;
    }

    public async Task<DbUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        if (_cachedUser is null)
            await RefreshCurrentUserAsync(ct);
        return _cachedUser!;
    }

    public async Task RefreshCurrentUserAsync(CancellationToken ct = default)
    {
        var identity = _identityService.GetCurrentUser();
        _cachedUser = await _userRepo.GetOrCreateUserAsync(identity, ct);
    }
}
