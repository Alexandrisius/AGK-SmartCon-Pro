using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IRevitCategoryLabelService"/> (#241):
/// a curated list of pickable model categories (MEP-focused plus common
/// architecture/model categories) labeled in the Revit session language.
/// Ordinals are the locale-invariant storage value for assignment
/// conditions. The list is computed lazily once per session — the Revit UI
/// language cannot change mid-session.
/// </summary>
/// <remarks>
/// <c>LabelUtils.GetLabelFor(BuiltInCategory)</c> exists only since Revit
/// 2020 — on a Revit 2019 runtime (R19 configuration) the direct call would
/// die at JIT with <see cref="MissingMethodException"/>. Resolution goes
/// through cached reflection with an enum-name fallback (pattern of #153,
/// see docs/multi-version-guide.md: an API that changed inside the
/// configuration range is resolved via reflection).
/// <para>
/// Threading/context: LabelUtils is a static resource-string lookup —
/// per Tammik (thebuildingcoder 0925) such utility classes "can be called
/// from any valid context with no need for an object instance"; the editor
/// dialog runs on Revit's main UI thread. Any failure degrades to the
/// enum-name fallback, never crashes.
/// </para>
/// </remarks>
public sealed class RevitCategoryLabelService : IRevitCategoryLabelService
{
    private static readonly Lazy<IReadOnlyList<RevitCategoryLabel>> Cached =
        new(() => BuildCategories(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Curated pickable categories — a stable, human-sized list of model
    /// categories families can belong to.
    /// </summary>
    private static readonly BuiltInCategory[] ModelCategories =
    {
        // MEP — piping
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_FlexPipeCurves,
        // MEP — HVAC
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_DuctTerminal,
        BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_Sprinklers,
        // MEP — electrical
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_ConduitFitting,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_LightingDevices,
        // Plumbing
        BuiltInCategory.OST_PlumbingFixtures,
        // Architecture / structure / model
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Stairs,
        BuiltInCategory.OST_Railings,
        BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_Furniture,
        BuiltInCategory.OST_Entourage,
    };

    private static readonly MethodInfo? GetLabelForMethod = typeof(LabelUtils).GetMethod(
        nameof(LabelUtils.GetLabelFor),
        new[] { typeof(BuiltInCategory) });

    public IReadOnlyList<RevitCategoryLabel> GetModelCategories() => Cached.Value;

    private static IReadOnlyList<RevitCategoryLabel> BuildCategories()
    {
        var labels = new List<RevitCategoryLabel>(ModelCategories.Length);
        foreach (var category in ModelCategories)
        {
            labels.Add(new RevitCategoryLabel((int)category, GetLabel(category)));
        }

        return labels;
    }

    private static string GetLabel(BuiltInCategory category)
    {
        if (GetLabelForMethod is not null)
        {
            try
            {
                return GetLabelForMethod.Invoke(null, new object[] { category }) as string ?? FallbackLabel(category);
            }
            catch (TargetInvocationException)
            {
                return FallbackLabel(category);
            }
        }

        return FallbackLabel(category);
    }

    private static string FallbackLabel(BuiltInCategory category)
    {
        var name = category.ToString();
        return name.StartsWith("OST_", StringComparison.Ordinal) ? name.Substring(4) : name;
    }

    /// <summary>
    /// Compile-time canary: if Autodesk removes/reshapes the
    /// <c>GetLabelFor(BuiltInCategory)</c> overload from the compiled SDK,
    /// this method fails to compile and forces a rework of the reflection
    /// resolution above. Never called at runtime (labels go through
    /// <see cref="GetLabelForMethod"/>; a direct call would JIT-crash on a
    /// Revit 2019 runtime).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string GetLabelDirect(BuiltInCategory category) => LabelUtils.GetLabelFor(category);
}
