using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ILoadableFamilyScanner
{
    IReadOnlyList<LoadableFamilyInfo> GetUniqueFamilies(Document activeDoc);
}
