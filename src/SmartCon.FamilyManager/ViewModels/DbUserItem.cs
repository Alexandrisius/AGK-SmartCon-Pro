using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed class DbUserItem
{
    public string UserId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string MachineId { get; init; } = string.Empty;
    public DbUserRole Role { get; set; }
    public DbUserStatus Status { get; set; }
    public bool IsOwnerRow { get; init; }
    public bool CanEditRole { get; init; }
    public bool CanDelete { get; init; }
    public IAsyncRelayCommand<DbUserRole> ChangeRoleCommand { get; }
    public IAsyncRelayCommand ToggleBanCommand { get; }
    public IAsyncRelayCommand DeleteUserCommand { get; }

    public DbUserItem(
        IAsyncRelayCommand<DbUserRole> changeRoleCommand,
        IAsyncRelayCommand toggleBanCommand,
        IAsyncRelayCommand deleteUserCommand)
    {
        ChangeRoleCommand = changeRoleCommand;
        ToggleBanCommand = toggleBanCommand;
        DeleteUserCommand = deleteUserCommand;
    }
}
