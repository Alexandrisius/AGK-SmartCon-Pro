namespace SmartCon.Core.Models.FamilyManager;

public sealed record LoadableFamilyInfo(
    string FamilyName,
    string FamilyUniqueId,
    string CategoryName,
    int TypeCount);
