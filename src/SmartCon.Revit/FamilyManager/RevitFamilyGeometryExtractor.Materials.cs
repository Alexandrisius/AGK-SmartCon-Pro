using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilyGeometryExtractor
{
    /// <summary>
    /// Resolves a <see cref="MaterialMeshGroup"/>'s <c>MaterialElementId</c>
    /// to a diffuse <see cref="Vector4"/> color. Issue #108 fix: this replaces
    /// <c>GetColorFromGeometry</c> which iterated the geometry and took the
    /// first face material (missing <c>GeometryInstance</c> for nested
    /// families). The new method receives the material id directly from
    /// <see cref="AddSolidWithMaterials"/> (which already traverses
    /// <c>GeometryInstance</c> via <see cref="CollectMeshWithMaterials"/>),
    /// so nested <c>FamilyInstance</c> faces now reach this resolver with
    /// their real material id instead of falling through to
    /// <c>FallbackColor</c>.
    /// </summary>
    /// <remarks>
    /// <b>Fallback chain</b> (Autodesk Revit API Developer Guide,
    /// "Element Material"): when <c>Face.MaterialElementId</c> is
    /// <c>InvalidElementId</c> the material is "By Category" — the API guide
    /// says: "If the material property is set to By Category in the UI, the
    /// ElementId for the material is ElementId.InvalidElementId and cannot be
    /// used to retrieve the Material object. Try retrieving the Material from
    /// Category." The chain below covers family documents where
    /// <c>element.Category</c> may be null:
    /// <list type="number">
    /// <item><description><paramref name="materialId"/> →
    /// <c>doc.GetElement(materialId) as Material</c> →
    /// <c>Material.Color</c>.</description></item>
    /// <item><description><c>element.Category.Material</c> (By Category for
    /// this element).</description></item>
    /// <item><description>For <c>FamilyInstance</c>:
    /// <c>inst.Symbol.Family.Category.Material</c> (nested family
    /// category).</description></item>
    /// <item><description><c>doc.OwnerFamily.Category.Material</c> (family
    /// document owner category).</description></item>
    /// <item><description><see cref="FallbackColor"/> (neutral gray
    /// 0.65).</description></item>
    /// </list>
    /// <para>
    /// <b>Appearance Asset Color</b> (Phase 2): <c>Material.Color</c> can be
    /// invalid for materials imported from Rhino/SAT (Autodesk forum
    /// 8948589). Reading the appearance asset's <c>generic_diffuse</c>
    /// property via <c>AppearanceAssetElement.GetRenderingAsset()</c> would
    /// recover the render color, but requires version-specific
    /// <c>AssetPropertyDoubleArray4d</c> handling. Out of scope for this fix
    /// — the vast majority of family materials carry a valid
    /// <c>Material.Color</c>.
    /// </para>
    /// </remarks>
    private static Vector4 GetColorForMaterialId(
        ElementId materialId, Document doc, Element element, string nodeName, bool verbose)
    {
        string? source = null;

        try
        {
            // 1. Face material (explicit material assignment on the face)
            if (materialId is not null && materialId != ElementId.InvalidElementId)
            {
                var material = doc.GetElement(materialId) as Material;
                if (material is not null &&
                    TryGetMaterialColor(material, out var r, out var g, out var b))
                {
                    var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                    source = "face material '" + material.Name + "'";
                    if (verbose)
                        SmartConLogger.Debug(
                            $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                            $"RGB({r},{g},{b}) → {result}");
                    return result;
                }
            }

            // 2. element.Category.Material (By Category for this element)
            try
            {
                var elemCat = element.Category;
                if (elemCat?.Material is { } catMat &&
                    TryGetMaterialColor(catMat, out var r, out var g, out var b))
                {
                    var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                    source = "element.Category '" + elemCat.Name + "' material '" + catMat.Name + "'";
                    if (verbose)
                        SmartConLogger.Debug(
                            $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                            $"RGB({r},{g},{b}) → {result}");
                    return result;
                }
            }
            catch { }

            // 3. FamilyInstance: inst.Symbol.Family.Category.Material
            //    (nested family category — the typical path for nested
            //    FamilyInstance elements whose own Category is null)
            if (element is FamilyInstance inst)
            {
                try
                {
                    var famCat = inst.Symbol?.Family?.Category;
                    if (famCat?.Material is { } famCatMat &&
                        TryGetMaterialColor(famCatMat, out var r, out var g, out var b))
                    {
                        var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                        source = "inst.Symbol.Family.Category '" + famCat.Name + "' material '" + famCatMat.Name + "'";
                        if (verbose)
                            SmartConLogger.Debug(
                                $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                                $"RGB({r},{g},{b}) → {result}");
                        return result;
                    }
                }
                catch { }
            }

            // 4. doc.OwnerFamily.Category.Material (family document owner)
            try
            {
                var ownerFamily = doc.OwnerFamily;
                if (ownerFamily?.Category is { } familyCat)
                {
                    var catMat = familyCat.Material;
                    if (catMat is not null &&
                        TryGetMaterialColor(catMat, out var r, out var g, out var b))
                    {
                        var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                        source = "OwnerFamily.Category '" + familyCat.Name + "' material '" + catMat.Name + "'";
                        if (verbose)
                            SmartConLogger.Debug(
                                $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                                $"RGB({r},{g},{b}) → {result}");
                        return result;
                    }
                }
            }
            catch { }

            if (verbose)
                SmartConLogger.Debug(
                    $"GetColorForMaterialId: '{nodeName}' → no face material, no category material → FallbackColor");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"GetColorForMaterialId: failed for '{nodeName}': {ex.Message} " +
                "[Action: using fallback gray color for this mesh]");
        }

        return FallbackColor;
    }

    /// <summary>
    /// Reads <c>Material.Color</c> defensively using the TryGet pattern.
    /// Returns <c>false</c> if the material is null, the color is null, or
    /// <c>Color.IsValid</c> is false (e.g. for materials imported from
    /// Rhino/SAT — Autodesk forum 8948589). On success, <paramref name="red"/>,
    /// <paramref name="green"/>, <paramref name="blue"/> are set to the
    /// material's 0-255 RGB values.
    /// </summary>
    private static bool TryGetMaterialColor(
        Material material, out byte red, out byte green, out byte blue)
    {
        red = 0; green = 0; blue = 0;
        try
        {
            var color = material.Color;
            if (color is not null && color.IsValid)
            {
                red = color.Red;
                green = color.Green;
                blue = color.Blue;
                return true;
            }
        }
        catch { }
        return false;
    }
}
