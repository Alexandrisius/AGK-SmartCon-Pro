using Autodesk.Revit.DB;
using SmartCon.Core.Math;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;

namespace SmartCon.Revit.Transform;

/// <summary>
/// Реализация ITransformService через Revit ElementTransformUtils.
/// Конвертирует Vec3 → XYZ на границе Core→Revit.
/// Все вызовы должны выполняться внутри открытой Transaction.
/// </summary>
public sealed class RevitTransformService : ITransformService
{
    public void MoveElement(Document doc, ElementId elementId, Vec3 offset)
    {
        if (VectorUtils.IsZero(offset)) return;

        ElementTransformUtils.MoveElement(doc, elementId, offset.ToXYZ());
    }

    public void RotateElement(Document doc, ElementId elementId,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians)
    {
        if (System.Math.Abs(angleRadians) < VectorUtils.Tolerance) return;

        var axis = Line.CreateBound(
            axisOrigin.ToXYZ(),
            (axisOrigin + axisDirection).ToXYZ());

        ElementTransformUtils.RotateElement(doc, elementId, axis, angleRadians);
    }

    public void MoveElements(Document doc, ICollection<ElementId> elementIds, Vec3 offset)
    {
        if (VectorUtils.IsZero(offset) || elementIds.Count == 0) return;

        ElementTransformUtils.MoveElements(doc, elementIds, offset.ToXYZ());
    }

    public void RotateElements(Document doc, ICollection<ElementId> elementIds,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians)
    {
        if (System.Math.Abs(angleRadians) < VectorUtils.Tolerance || elementIds.Count == 0) return;

        var axis = Line.CreateBound(
            axisOrigin.ToXYZ(),
            (axisOrigin + axisDirection).ToXYZ());

        ElementTransformUtils.RotateElements(doc, elementIds, axis, angleRadians);
    }
}
