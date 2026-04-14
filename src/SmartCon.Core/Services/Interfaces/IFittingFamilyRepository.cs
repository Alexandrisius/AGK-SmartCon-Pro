using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Retrieval of fitting families eligible for PipeConnect mapping.
/// Criteria: OST_PipeFitting + PartType=MultiPort + exactly 2 ConnectorElements.
/// Implementation: SmartCon.Revit/Family/FittingFamilyRepository.cs
/// </summary>
public interface IFittingFamilyRepository
{
    /// <summary>
    /// Returns "Pipe Fitting" families with PartType=MultiPort
    /// and exactly 2 connectors. Call OUTSIDE transaction (EditFamily requires IsModifiable=false).
    /// </summary>
    IReadOnlyList<FamilyInfo> GetEligibleFittingFamilies(Document doc);
}
