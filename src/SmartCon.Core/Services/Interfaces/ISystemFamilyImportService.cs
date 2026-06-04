using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyImportService
{
    IReadOnlyList<SystemFamilyPendingImport> PickAndPrepare();

    /// <summary>
    /// Анализирует активный проект (14 системных категорий), копирует размещённые типы
    /// в новый .rvt с placement инстансов на сетке 2×2м. Возвращает список pending imports
    /// (один на категорию с ненулевым числом размещённых типов).
    /// Должен вызываться ВНУТРИ ExternalEvent (Revit UI thread).
    /// </summary>
    IReadOnlyList<SystemFamilyPendingImport> AnalyzeAndPrepareForProject(Document activeDoc);

    Task<SystemFamilyImportResult> ImportBatchItemsAsync(IReadOnlyList<FamilyBatchImportItem> items);
}

public sealed record SystemFamilyPendingImport(
    string CategoryName,
    IReadOnlyList<SelectedSystemType> Types,
    string TempRvtPath);
