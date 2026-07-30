using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Synchronizes a material in the active project with the same-named
/// material from the source mini-project (Issue #104). Materials are never
/// copied between documents (Revit 2024+ duplicates them unconditionally) —
/// they are matched by name, updated in place (graphics, appearance,
/// physical and thermal assets) or created when missing.
/// </summary>
/// <remarks>
/// Must be called on the Revit main thread (I-01) and inside an open
/// transaction (appearance assets commit through
/// <c>AppearanceAssetEditScope</c>, which requires one).
/// <para>Asymmetry by design: when the source material has no structural /
/// thermal asset (or no appearance asset), the target's corresponding asset
/// is kept — an absent asset in the reference means "not configured", not
/// "must be cleared", because clearing asset containers risks affecting
/// unrelated analyses in the project.</para>
/// </remarks>
public interface IMaterialSyncService
{
    /// <summary>
    /// Ensure the active project contains a material named
    /// <paramref name="materialName"/> whose data matches the same-named
    /// material in <paramref name="sourceDoc"/>. Returns the project
    /// material's id, or <c>null</c> when the source mini-project has no
    /// material with this name.
    /// </summary>
    ElementId? SyncMaterial(Document sourceDoc, Document activeDoc, string materialName);
}
