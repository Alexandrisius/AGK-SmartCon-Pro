namespace SmartCon.Core.Models.FamilyManager;

public sealed record DbUser(
    string UserId,
    string DisplayName,
    DbUserRole Role,
    DbUserStatus Status,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset LastSeenAtUtc
);
