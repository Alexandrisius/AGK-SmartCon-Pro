using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services;

public sealed class DbAccessControlService : IDbAccessControlService
{
    private readonly IDbUserRepository _userRepo;
    private readonly IUserIdentityService _identityService;
    private readonly LocalCatalogDatabase _database;
    private readonly IDatabaseCompatibilityService _compatibility;
    private readonly CloudDatabaseGate _cloudGate;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile DbUser? _cachedUser;

    public DbAccessControlService(
        IDbUserRepository userRepo,
        IUserIdentityService identityService,
        LocalCatalogDatabase database,
        IDatabaseCompatibilityService compatibility,
        CloudDatabaseGate? cloudGate = null)
    {
        _userRepo = userRepo;
        _identityService = identityService;
        _database = database;
        _compatibility = compatibility;
        // Optional for unit tests (detached gate = never blocks); DI injects
        // the process-wide singleton updated by DatabaseManager.
        _cloudGate = cloudGate ?? new CloudDatabaseGate();
    }

    /// <summary>
    /// ADR-075 §7: a Subscribed cloud copy is read-only regardless of role —
    /// sync is the only writer. ANDs into every write capability.
    /// </summary>
    public bool IsCloudReadOnly => _cloudGate.IsWriteBlocked;

    // ADR-058 (#173): every write capability ANDs the plugin-compat gate —
    // a database upgraded by a newer SmartCon is read-only for this plugin
    // regardless of role (tiered model: catalog writes blocked, loads allowed
    // via CanLoadToProject which stays role-only). ADR-075 §7: the cloud
    // Subscribed gate ANDs in the same way.
    public bool CanImport => IsEditorRole && !_compatibility.IsDatabaseNewerThanPlugin && !IsCloudReadOnly;

    public bool CanEdit => IsEditorRole && !_compatibility.IsDatabaseNewerThanPlugin && !IsCloudReadOnly;

    public bool CanManageUsers
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Status != DbUserStatus.Banned
                && snapshot?.Role == DbUserRole.Owner
                && !_compatibility.IsDatabaseNewerThanPlugin
                && !IsCloudReadOnly;
        }
    }

    public bool IsEditorRole
    {
        get
        {
            var snapshot = _cachedUser;
            return snapshot?.Status != DbUserStatus.Banned
                && snapshot?.Role is DbUserRole.Owner or DbUserRole.BimMaster;
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

        // ADR-075 §7: auto-register db_users is disabled on a Subscribed copy
        // (sync is the only writer) — every local user is a read-only Engineer.
        if (IsCloudReadOnly)
            return CloudSubscriberUser(identity);

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

        // ADR-075 §7: no db_users writes on a Subscribed copy (see GetCurrentUserAsync).
        if (IsCloudReadOnly)
        {
            _cachedUser = CloudSubscriberUser(identity);
            _database.SetWriteAccess(false);
            return;
        }

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
        // ADR-075 §7: no blanket write re-enable — the Subscribed gate ANDs in.
        _database.SetWriteAccess(!IsCloudReadOnly);
    }

    private static DbUser CloudSubscriberUser(UserIdentity identity)
    {
        var now = DateTimeOffset.UtcNow;
        return new DbUser(identity.UserId, identity.DisplayName, DbUserRole.Engineer, DbUserStatus.Active, now, now);
    }

    private void ApplyWriteAccess(DbUser user)
    {
        // ADR-058 (#173): a database upgraded by a newer plugin is read-only
        // for this plugin regardless of role — the compat flag ANDs into
        // every role-based write decision. ADR-075 §7: so does the cloud
        // Subscribed gate (sync is the only writer of the copy).
        var canWrite = user.Status != DbUserStatus.Banned
            && user.Role is DbUserRole.Owner or DbUserRole.BimMaster
            && !_compatibility.IsDatabaseNewerThanPlugin
            && !IsCloudReadOnly;
        _database.SetWriteAccess(canWrite);
    }
}
