using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services;

public sealed class DbAccessControlService : IDbAccessControlService
{
    private readonly IDbUserRepository _userRepo;
    private readonly IUserIdentityService _identityService;
    private readonly LocalCatalogDatabase _database;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile DbUser? _cachedUser;

    public DbAccessControlService(IDbUserRepository userRepo, IUserIdentityService identityService, LocalCatalogDatabase database)
    {
        _userRepo = userRepo;
        _identityService = identityService;
        _database = database;
    }

    public bool CanImport
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Status != DbUserStatus.Banned && snapshot?.Role is DbUserRole.Owner or DbUserRole.BimMaster;
        }
    }

    public bool CanEdit
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Status != DbUserStatus.Banned && snapshot?.Role is DbUserRole.Owner or DbUserRole.BimMaster;
        }
    }

    public bool CanManageUsers
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Status != DbUserStatus.Banned && snapshot?.Role == DbUserRole.Owner;
        }
    }

    public bool CanLoadToProject
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Status != DbUserStatus.Banned;
        }
    }

    public bool IsOwner
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Role == DbUserRole.Owner;
        }
    }

    public bool IsBanned
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Status == DbUserStatus.Banned;
        }
    }

    public async Task<DbUserRole> GetCurrentUserRoleAsync(CancellationToken ct = default)
    {
        var user = await GetCurrentUserAsync(ct);
        return user.Role;
    }

    public async Task<DbUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var current = _cachedUser;
        if (current is not null)
            return current;

        // Revit API: должен выполняться на UI-потоке. Не переносить за await!
        var identity = _identityService.GetCurrentUser();

        await _refreshLock.WaitAsync(ct);
        try
        {
            current = _cachedUser;
            if (current is not null)
                return current;

            var user = await _userRepo.GetOrCreateUserAsync(identity, ct);
            _cachedUser = user;
            ApplyWriteAccess(user);
            return user;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task RefreshCurrentUserAsync(CancellationToken ct = default)
    {
        // Revit API: должен выполняться на UI-потоке. Не переносить за await!
        var identity = _identityService.GetCurrentUser();

        await _refreshLock.WaitAsync(ct);
        try
        {
            var user = await _userRepo.GetOrCreateUserAsync(identity, ct);
            _cachedUser = user;
            ApplyWriteAccess(user);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void InvalidateCache()
    {
        _cachedUser = null;
        _database.SetWriteAccess(true);
    }

    private void ApplyWriteAccess(DbUser user)
    {
        var canWrite = user.Status != DbUserStatus.Banned && user.Role is DbUserRole.Owner or DbUserRole.BimMaster;
        _database.SetWriteAccess(canWrite);
    }
}
