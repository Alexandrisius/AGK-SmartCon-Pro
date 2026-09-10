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
    /// Resolves the integer value of a Revit <see cref="ElementId"/> across
    /// target frameworks. The IntegerValue property is deprecated in
    /// Revit 2024+ in favor of <see cref="ElementId.Value"/> (which returns
    /// a long). Use of this helper avoids CS0618 across the multi-version build.
    /// </summary>
    private static int GetElementIdInt(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return (int)id.Value;
#else
        return id.IntegerValue;
#endif
    }

    private static int? GetIsVisibleParam(Element element)
    {
        try
        {
            var p = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            if (p is null || !p.HasValue) return null;
            return p.AsInteger();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString() : "<null>";

    /// <summary>
    /// Checks whether a family element is visible at the given detail level.
    /// See #102 for root cause and rationale.
    /// </summary>
    /// <param name="element">A <see cref="GenericForm"/> or nested
    /// <see cref="FamilyInstance"/> in a family document.</param>
    /// <param name="level">Detail level to test (typically
    /// <see cref="ViewDetailLevel.Fine"/> for 3D preview).</param>
    /// <param name="reason">Human-readable reason when element is not visible
    /// at <paramref name="level"/>; <c>null</c> when element is visible or on
    /// fail-open.</param>
    /// <returns><c>true</c> if element is shown at <paramref name="level"/>;
    /// otherwise <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Background:</b> <see cref="Options.IncludeNonVisibleObjects"/> = true
    /// recovers conditionally-visible solids (see commit 5283765) but bypasses
    /// Revit's detail-level filtering. Nested family instances intended only
    /// for Coarse detail level (e.g. "Низкая детализация" symbolic graphics)
    /// would otherwise leak into the Fine-detail 3D preview (see #102).
    /// </para>
    /// <para>
    /// <b>Two paths:</b>
    /// <list type="bullet">
    /// <item><description><see cref="GenericForm"/>: uses
    /// <see cref="GenericForm.GetVisibility()"/> returning a typed
    /// <see cref="FamilyElementVisibility"/> with
    /// <c>IsShownInCoarse/Medium/Fine</c>.</description></item>
    /// <item><description><see cref="FamilyInstance"/> / other: reads
    /// <see cref="BuiltInParameter.GEOM_VISIBILITY_PARAM"/> as a bitfield
    /// (Coarse = 1 &lt;&lt; 13 = 8192, Medium = 1 &lt;&lt; 14 = 16384,
    /// Fine = 1 &lt;&lt; 15 = 32768). Value <c>0</c> means "detail component"
    /// with unconditional visibility (Tammik / Autodesk forum / RevitLookup).
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Fail-open:</b> if neither the typed visibility nor the parameter is
    /// available, the element is considered visible — never silently drop
    /// geometry.
    /// </para>
    /// </remarks>
    private static bool IsShownAtDetailLevel(
        Element element, ViewDetailLevel level, out string? reason)
    {
        reason = null;

        // Path 1: GenericForm — typed visibility API
        if (element is GenericForm form)
        {
            FamilyElementVisibility? vis;
            try
            {
                vis = form.GetVisibility();
            }
            catch (Exception ex)
            {
                reason = $"GetVisibility() threw {ex.GetType().Name}";
                return true;
            }

            if (vis is null)
            {
                reason = "GetVisibility() returned null";
                return true;
            }

            bool shown = level switch
            {
                ViewDetailLevel.Coarse => vis.IsShownInCoarse,
                ViewDetailLevel.Medium => vis.IsShownInMedium,
                ViewDetailLevel.Fine => vis.IsShownInFine,
                _ => true
            };

            if (!shown)
            {
                reason = $"GenericForm visibility " +
                         $"Coarse={vis.IsShownInCoarse} " +
                         $"Medium={vis.IsShownInMedium} " +
                         $"Fine={vis.IsShownInFine}";
            }

            return shown;
        }

        // Path 2: FamilyInstance / other — GEOM_VISIBILITY_PARAM bitfield
        Parameter? p;
        try
        {
            p = element.get_Parameter(BuiltInParameter.GEOM_VISIBILITY_PARAM);
        }
        catch (Exception ex)
        {
            reason = $"get_Parameter(GEOM_VISIBILITY_PARAM) threw {ex.GetType().Name}";
            return true;
        }

        if (p is null)
        {
            reason = "GEOM_VISIBILITY_PARAM not found";
            return true;
        }

        int visValue;
        try
        {
            visValue = p.AsInteger();
        }
        catch (Exception ex)
        {
            reason = $"AsInteger() threw {ex.GetType().Name}";
            return true;
        }

        if (visValue == 0)
        {
            // Detail component family — unconditional visibility across all
            // detail levels (Tammik / Autodesk forum / RevitLookup).
            return true;
        }

        const int CoarseBit = 1 << 13; // 8192
        const int MediumBit = 1 << 14; // 16384
        const int FineBit   = 1 << 15; // 32768

        bool result = level switch
        {
            ViewDetailLevel.Coarse => (visValue & CoarseBit) != 0,
            ViewDetailLevel.Medium => (visValue & MediumBit) != 0,
            ViewDetailLevel.Fine   => (visValue & FineBit)   != 0,
            _ => true
        };

        if (!result)
        {
            reason = $"GEOM_VISIBILITY_PARAM value={visValue} " +
                     $"(Coarse={(visValue & CoarseBit) != 0}, " +
                     $"Medium={(visValue & MediumBit) != 0}, " +
                     $"Fine={(visValue & FineBit) != 0})";
        }

        return result;
    }
}
