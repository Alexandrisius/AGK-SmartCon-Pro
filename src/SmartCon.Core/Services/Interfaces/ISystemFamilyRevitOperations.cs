using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyRevitOperations
{
    IReadOnlyList<SelectedSystemType> PickSystemTypes();
    CreateCleanProjectResult CreateCleanProjectWithTypes(IReadOnlyList<string> typeUniqueIds);
}
