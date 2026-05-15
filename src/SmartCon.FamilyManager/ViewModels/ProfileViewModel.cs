using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty] private DbUser? _currentUser;
    [ObservableProperty] private UserIdentity? _currentUserIdentity;
    [ObservableProperty] private bool _isOwner;
    [ObservableProperty] private int _userCount;
    [ObservableProperty] private string _initials = string.Empty;
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
        IsOwner = _accessControl.IsOwner;
        Initials = GetInitials(CurrentUser.DisplayName);
        await LoadUsersAsync(ct);
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
                changeRoleCommand: new AsyncRelayCommand<DbUserRole>(role => ChangeRoleAsync(userId, role, ct)),
                toggleBanCommand: new AsyncRelayCommand(() => ToggleBanAsync(userId, u.Status == DbUserStatus.Active, ct)),
                deleteUserCommand: new AsyncRelayCommand(() => DeleteUserAsync(userId, u.DisplayName, ct)))
            {
                UserId = userId,
                DisplayName = u.DisplayName,
                MachineId = userId,
                Role = u.Role,
                Status = u.Status,
                IsOwnerRow = isOwnerRow,
                CanEditRole = !isOwnerRow && IsOwner,
                CanDelete = !isOwnerRow && IsOwner,
            });
        }

        Users = new ObservableCollection<DbUserItem>(items);
    }

    private async Task ChangeRoleAsync(string userId, DbUserRole? newRole, CancellationToken ct)
    {
        if (newRole is null) return;
        await _userRepo.UpdateUserRoleAsync(userId, newRole.Value, ct);
        await LoadUsersAsync(ct);
    }

    private async Task ToggleBanAsync(string userId, bool currentlyActive, CancellationToken ct)
    {
        var newStatus = currentlyActive ? DbUserStatus.Banned : DbUserStatus.Active;
        await _userRepo.UpdateUserStatusAsync(userId, newStatus, ct);
        await LoadUsersAsync(ct);
    }

    private async Task DeleteUserAsync(string userId, string displayName, CancellationToken ct)
    {
        var confirm = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_ProfileDeleteTitle) ?? "Remove User",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ProfileDeletePrompt) ?? "Remove \"{0}\" from this database?\nThey will be able to reconnect as Engineer.",
                displayName));
        if (!confirm) return;

        await _userRepo.RemoveUserAsync(userId, ct);
        await LoadUsersAsync(ct);
    }

    [RelayCommand]
    private async Task TransferOwnershipAsync(CancellationToken ct)
    {
        var bimMasters = Users.Where(u => u.Role == DbUserRole.BimMaster).ToList();
        if (bimMasters.Count == 0)
        {
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_TransferTitle) ?? "Transfer Ownership",
                LanguageManager.GetString(StringLocalization.Keys.FM_TransferNoBimMaster) ?? "No BIM Masters to transfer ownership to.");
            return;
        }

        var confirm = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_TransferTitle) ?? "Transfer Ownership",
            LanguageManager.GetString(StringLocalization.Keys.FM_TransferConfirm) ?? "You will become a BIM Master. Continue?");
        if (!confirm) return;

        var targetUser = bimMasters[0];
        var success = await _userRepo.TransferOwnershipAsync(CurrentUser!.UserId, targetUser.UserId, ct);
        if (success)
        {
            await InitializeAsync(ct);
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
