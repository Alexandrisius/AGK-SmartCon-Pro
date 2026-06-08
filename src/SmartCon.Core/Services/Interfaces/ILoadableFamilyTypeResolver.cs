using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ILoadableFamilyTypeResolver
{
    IReadOnlyList<FamilyTypeDescriptor> ResolveTypesFromRfa(
        string rfaFilePath,
        string catalogItemId,
        string? versionId = null,
        string? fileId = null);
}
