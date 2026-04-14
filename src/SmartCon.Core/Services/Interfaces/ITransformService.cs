using Autodesk.Revit.DB;
using SmartCon.Core.Math;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Element transformation: move and rotate.
/// Works with Vec3 at the Core-Revit boundary (Vec3-to-XYZ conversion inside implementation).
/// Implementation: SmartCon.Revit/Transform/RevitTransformService.cs
/// </summary>
public interface ITransformService
{
    /// <summary>
    /// Move element by offset vector.
    /// </summary>
    void MoveElement(Document doc, ElementId elementId, Vec3 offset);

    /// <summary>
    /// Rotate element around an axis (defined by point and direction) by angle in radians.
    /// </summary>
    void RotateElement(Document doc, ElementId elementId,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians);

    /// <summary>
    /// Move a set of elements by offset vector.
    /// </summary>
    void MoveElements(Document doc, ICollection<ElementId> elementIds, Vec3 offset);

    /// <summary>
    /// Rotate a set of elements around an axis by an angle.
    /// </summary>
    void RotateElements(Document doc, ICollection<ElementId> elementIds,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians);
}
