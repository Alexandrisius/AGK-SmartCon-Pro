namespace SmartCon.Core.Models.FamilyManager;

public sealed record SelectedElementsAnalysis(
    IReadOnlyList<SelectedSystemType> SystemTypes,
    IReadOnlyList<LoadableFamilyInfo> LoadableFamilies)
{
    public int TotalCount => SystemTypes.Count + LoadableFamilies.Count;

    public bool IsEmpty => TotalCount == 0;
}
