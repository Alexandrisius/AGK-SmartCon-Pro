using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class ProfileViewModel : ObservableObject, SmartCon.Core.Services.Interfaces.IObservableRequestClose
{
    private readonly IDbUserRepository _userRepo;
    private readonly IDbAccessControlService _accessControl;
    private readonly IUserIdentityService _identityService;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    [ObservableProperty] private DbUser? _currentUser;
    [ObservableProperty] private UserIdentity? _currentUserIdentity;
    [ObservableProperty] private bool _isOwner;
    [ObservableProperty] private int _userCount;
    [ObservableProperty] private string _initials = string.Empty;
    [ObservableProperty] private string _currentRoleDisplayName = string.Empty;
    [ObservableProperty] private string _currentRoleDescription = string.Empty;
    [ObservableProperty] private string _currentRoleLabel = string.Empty;
    [ObservableProperty] private DbUserRole _currentRole;
    [ObservableProperty] private ObservableCollection<DbUserItem> _users = [];

    public event Action<bool?>? RequestClose;

    public ProfileViewModel(
        IDbUserRepository userRepo,
        IDbAccessControlService accessControl,
        IUserIdentityService identityService,
        IFamilyManagerDialogService dialogService)
    {
        _userRepo = userRepo;
        _accessControl = accessControl;
        _identityService = identityService;
        _dialogService = dialogService;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        CurrentUserIdentity = _identityService.GetCurrentUser();
        CurrentUser = await _accessControl.GetCurrentUserAsync(ct);
        if (CurrentUser is null)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied",
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDeniedMessage) ?? "Unable to retrieve current user.");
            RequestClose?.Invoke(false);
            return;
        }

        IsOwner = _accessControl.IsOwner;
        CurrentRole = CurrentUser.Role;
        Initials = GetInitials(CurrentUser.DisplayName);
        CurrentRoleDisplayName = GetRoleDisplayName(CurrentUser.Role);
        CurrentRoleDescription = GetRoleDescription(CurrentUser.Role);
        CurrentRoleLabel = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_ProfileYourRole) ?? "Your role: {0}",
            CurrentRoleDisplayName);
        await LoadUsersAsync(ct);
    }

    private static string GetRoleDisplayName(DbUserRole role)
    {
        var key = role switch
        {
            DbUserRole.Owner => StringLocalization.Keys.FM_RoleOwner,
            DbUserRole.BimMaster => StringLocalization.Keys.FM_RoleBimMaster,
            _ => StringLocalization.Keys.FM_RoleEngineer,
        };
        return LanguageManager.GetString(key) ?? role.ToString();
    }

    private static string GetRoleDescription(DbUserRole role)
    {
        var key = role switch
        {
            DbUserRole.BimMaster => StringLocalization.Keys.FM_ProfileBimMasterInfo,
            _ => StringLocalization.Keys.FM_ProfileEngineerInfo,
        };
        return LanguageManager.GetString(key) ?? string.Empty;
    }

    private async Task LoadUsersAsync(CancellationToken ct = default)
    {
        var dbUsers = await _userRepo.GetAllUsersAsync(ct);
        UserCount = dbUsers.Count;

        var items = new List<DbUserItem>();
        foreach (var u in dbUsers)
        {
            var userId = u.UserId;
            var isOwnerRow = u.Role == DbUserRole.Owner;

            items.Add(new DbUserItem(
                changeRoleCommand: new AsyncRelayCommand(() => ChangeRoleAsync(userId, default)),
                toggleBanCommand: new AsyncRelayCommand(() => ToggleBanAsync(userId, u.Status == DbUserStatus.Active, default)))
            {
                UserId = userId,
                DisplayName = u.DisplayName,
                MachineId = userId,
                Role = u.Role,
                Status = u.Status,
                IsOwnerRow = isOwnerRow,
                CanEditRole = !isOwnerRow && IsOwner && u.Status != DbUserStatus.Banned,
                CanToggleBan = !isOwnerRow && IsOwner,
            });
        }

        Users = new ObservableCollection<DbUserItem>(items);
    }

    private async Task ChangeRoleAsync(string userId, CancellationToken ct)
    {
        var userItem = Users.FirstOrDefault(u => u.UserId == userId);
        if (userItem is null) return;
        var newRole = userItem.Role;

        await _operationLock.WaitAsync(ct);
        try
        {
            if (newRole == DbUserRole.Owner)
            {
                var confirm = _dialogService.ShowConfirmation(
                    LanguageManager.GetString(StringLocalization.Keys.FM_TransferTitle) ?? "Transfer Ownership",
                    LanguageManager.GetString(StringLocalization.Keys.FM_TransferConfirm) ?? "You will become a BIM Master. Continue?");
                if (!confirm) return;

                var currentUserId = CurrentUser?.UserId;
                if (string.IsNullOrEmpty(currentUserId))
                    return;

                var success = await _userRepo.TransferOwnershipAsync(currentUserId!, userId, ct);
                if (success)
                {
                    await InitializeAsync(ct);
                }
                return;
            }

            await _userRepo.UpdateUserRoleAsync(userId, newRole, ct);
            await LoadUsersAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"ChangeRoleAsync failed: {ex}");
            _dialogService.ShowError("Error", ex.Message);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    [RelayCommand]
    private async Task ConfirmRoleAsync()
    {
        if (CurrentUser is null) return;
        await ChangeRoleAsync(CurrentUser.UserId, default);
    }

    private async Task ToggleBanAsync(string userId, bool currentlyActive, CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            var newStatus = currentlyActive ? DbUserStatus.Banned : DbUserStatus.Active;
            await _userRepo.UpdateUserStatusAsync(userId, newStatus, ct);
            await LoadUsersAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"ToggleBanAsync failed: {ex}");
            _dialogService.ShowError("Error", ex.Message);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(true);
    }

    private static string GetInitials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "?";
        var parts = displayName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
        return displayName.Length >= 2 ? displayName[..2].ToUpperInvariant() : displayName.ToUpperInvariant();
    }
}
