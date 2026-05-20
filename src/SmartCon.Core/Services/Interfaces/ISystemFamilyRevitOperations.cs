namespace SmartCon.Core.Services.Interfaces;

public record SelectedPipeType(string UniqueId, string Name);

public record CreateCleanProjectResult(bool Success, string? FilePath, string? Error, int CopiedElementsCount);

public interface ISystemFamilyRevitOperations
{
    IReadOnlyList<SelectedPipeType> PickPipeTypes();
    CreateCleanProjectResult CreateCleanProjectWithTypes(IReadOnlyList<string> pipeTypeUniqueIds);
}
