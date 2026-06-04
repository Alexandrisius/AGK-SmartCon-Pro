using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyRevitOperations
{
    IReadOnlyList<SelectedSystemType> PickSystemTypes();

    /// <summary>
    /// Сканирует активный проект и возвращает список категорий системных семейств,
    /// в которых есть размещённые в модели инстансы. Возвращает ТОЛЬКО категории
    /// с ненулевым числом типов. Только размещённые типы (WhereElementIsNotElementType).
    /// </summary>
    IReadOnlyList<CategoryAnalysis> AnalyzeActiveProject(Document activeDoc);

    /// <summary>
    /// Создаёт чистый проект, копирует в него указанные типы (по UniqueId из sourceDoc),
    /// размещает инстансы каждого типа на сетке 2×2 м (Level 1) и сохраняет.
    /// Source = sourceDoc, destination = новый проект (UnitSystem.Metric).
    /// </summary>
    CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName);

    /// <summary>
    /// Legacy: создаёт чистый проект с типами, без размещения инстансов.
    /// Используется старым SystemFamilyImportService (picker flow) — оставлен для обратной совместимости.
    /// </summary>
    CreateCleanProjectResult CreateCleanProjectWithTypes(IReadOnlyList<string> typeUniqueIds);
}
