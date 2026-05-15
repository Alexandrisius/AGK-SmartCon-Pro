namespace SmartCon.Core.Models.FamilyManager;

public sealed record UserIdentity(
    string UserId,
    string DisplayName,
    string MachineName,
    string UserName
);
