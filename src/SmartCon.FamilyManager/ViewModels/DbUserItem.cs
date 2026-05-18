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
    public bool IsBanned => Status == DbUserStatus.Banned;
    public bool IsOwnerRow { get; init; }
    public bool CanEditRole { get; init; }
    public bool CanToggleBan { get; init; }
    public IAsyncRelayCommand ChangeRoleCommand { get; }
    public IAsyncRelayCommand ToggleBanCommand { get; }

    public DbUserItem(
        IAsyncRelayCommand changeRoleCommand,
        IAsyncRelayCommand toggleBanCommand)
    {
        ChangeRoleCommand = changeRoleCommand;
        ToggleBanCommand = toggleBanCommand;
    }
}
