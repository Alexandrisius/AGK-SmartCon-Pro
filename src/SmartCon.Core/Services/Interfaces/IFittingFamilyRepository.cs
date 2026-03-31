using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Получение семейств фитингов, подходящих для маппинга PipeConnect.
/// Критерии: OST_PipeFitting + PartType=MultiPort + ровно 2 ConnectorElement.
/// Реализация: SmartCon.Revit/Family/FittingFamilyRepository.cs
/// </summary>
public interface IFittingFamilyRepository
{
    /// <summary>
    /// Возвращает семейства "Соединительные детали трубопроводов" с PartType=MultiPort
    /// и ровно 2 коннекторами. Вызывать ВНЕ транзакции (EditFamily требует IsModifiable=false).
    /// </summary>
    IReadOnlyList<FamilyInfo> GetEligibleFittingFamilies(Document doc);
}
