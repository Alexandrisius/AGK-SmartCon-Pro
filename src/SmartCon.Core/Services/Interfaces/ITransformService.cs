using Autodesk.Revit.DB;
using SmartCon.Core.Math;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Трансформация элементов: перемещение и поворот.
/// Работает с Vec3 на границе Core→Revit (конвертация Vec3↔XYZ внутри реализации).
/// Реализация: SmartCon.Revit/Transform/RevitTransformService.cs
/// </summary>
public interface ITransformService
{
    /// <summary>
    /// Переместить элемент на вектор offset.
    /// </summary>
    void MoveElement(Document doc, ElementId elementId, Vec3 offset);

    /// <summary>
    /// Повернуть элемент вокруг оси (определяемой точкой и направлением) на угол в радианах.
    /// </summary>
    void RotateElement(Document doc, ElementId elementId,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians);

    /// <summary>
    /// Переместить набор элементов на вектор offset.
    /// </summary>
    void MoveElements(Document doc, ICollection<ElementId> elementIds, Vec3 offset);

    /// <summary>
    /// Повернуть набор элементов вокруг оси на угол.
    /// </summary>
    void RotateElements(Document doc, ICollection<ElementId> elementIds,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians);
}
