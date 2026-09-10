using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IMaterialSyncService"/> (Issue #104).
/// Materials are matched by name and updated in place — never copied between
/// documents (Revit 2024+ duplicates copied materials unconditionally).
/// Physical/thermal assets travel as self-contained value objects via
/// <c>PropertySetElement.GetStructuralAsset/GetThermalAsset</c>; when the
/// target asset container is shared with other materials a new container is
/// created so foreign materials are never touched. Appearance is edited
/// through <see cref="AppearanceAssetEditScope"/> after detaching the
/// material from a shared appearance asset.
/// </summary>
public sealed class RevitMaterialSyncService : IMaterialSyncService
{
    public ElementId? SyncMaterial(Document sourceDoc, Document activeDoc, string materialName)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(sourceDoc);
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(materialName);
#else
        if (sourceDoc is null) throw new ArgumentNullException(nameof(sourceDoc));
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (materialName is null) throw new ArgumentNullException(nameof(materialName));
#endif

        using var _scope = SmartConLogger.BeginScope(
            "SystemSync",
            ("Method", nameof(SyncMaterial)),
            ("MaterialName", materialName));

        var source = FindMaterialByName(sourceDoc, materialName);
        if (source is null)
        {
            SmartConLogger.Debug($"Material '{materialName}' not found in the source mini-project.");
            return null;
        }

        var target = FindMaterialByName(activeDoc, materialName);
        if (target is null)
        {
            target = CreateMaterial(activeDoc, materialName);
            if (target is null)
            {
                SmartConLogger.Warn(
                    $"Material '{materialName}': creation failed (no prototype material in the project). " +
                    "[Action: create the material in the project manually, then re-run the sync]");
                return null;
            }
        }

        SyncGraphics(sourceDoc, activeDoc, source, target);
        SyncAppearance(source, target);
        SyncStructuralAsset(source, target);
        SyncThermalAsset(source, target);
        return target.Id;
    }

    public ElementId? EnsureMaterial(Document activeDoc, string materialName)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(materialName);
#else
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (materialName is null) throw new ArgumentNullException(nameof(materialName));
#endif

        var existing = FindMaterialByName(activeDoc, materialName);
        if (existing is not null) return existing.Id;

        var created = CreateMaterial(activeDoc, materialName);
        if (created is null)
        {
            SmartConLogger.Warn(
                $"Material '{materialName}': creation failed (no prototype material in the project). " +
                "[Action: create the material in the project manually, then re-run the sync]");
            return null;
        }
        return created.Id;
    }

    private static Material? FindMaterialByName(Document doc, string name)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Material));
        return collector.Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static Material? CreateMaterial(Document doc, string name)
    {
        // A material created via Material.Create has no appearance asset —
        // duplicating a prototype keeps the material fully editable.
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Material));
        var prototype = collector.Cast<Material>()
            .FirstOrDefault(m => m.AppearanceAssetId is not null &&
                                 m.AppearanceAssetId != ElementId.InvalidElementId)
            ?? new FilteredElementCollector(doc).OfClass(typeof(Material))
                .Cast<Material>().FirstOrDefault();
        try
        {
            if (prototype is not null)
            {
                return prototype.Duplicate(name);
            }
            var createdId = Material.Create(doc, name);
            return doc.GetElement(createdId) as Material;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"CreateMaterial '{name}': {ex.Message}");
            return null;
        }
    }

    private static void SyncGraphics(Document sourceDoc, Document activeDoc, Material source, Material target)
    {
        try { target.Color = source.Color; } catch (Exception ex) { LogSkip("Color", ex); }
        try { target.Transparency = source.Transparency; } catch (Exception ex) { LogSkip("Transparency", ex); }
        try { target.Shininess = source.Shininess; } catch (Exception ex) { LogSkip("Shininess", ex); }
        try { target.Smoothness = source.Smoothness; } catch (Exception ex) { LogSkip("Smoothness", ex); }
        try { target.UseRenderAppearanceForShading = source.UseRenderAppearanceForShading; }
        catch (Exception ex) { LogSkip("UseRenderAppearanceForShading", ex); }

        try
        {
            var surfacePattern = ResolvePattern(
                sourceDoc, activeDoc, source.SurfaceForegroundPatternId);
            // null = source pattern not found in the project — keep the
            // target pattern instead of silently wiping it to <none>.
            if (surfacePattern is not null) target.SurfaceForegroundPatternId = surfacePattern;
        }
        catch (Exception ex) { LogSkip("SurfaceForegroundPattern", ex); }
        try
        {
            var cutPattern = ResolvePattern(
                sourceDoc, activeDoc, source.CutForegroundPatternId);
            if (cutPattern is not null) target.CutForegroundPatternId = cutPattern;
        }
        catch (Exception ex) { LogSkip("CutForegroundPattern", ex); }
    }

    /// <summary>
    /// <c>InvalidElementId</c> — the source has no pattern (target must be
    /// cleared); the resolved id — matched by name; <c>null</c> — the source
    /// pattern is missing in the project (caller keeps the target value).
    /// </summary>
    private static ElementId? ResolvePattern(Document sourceDoc, Document activeDoc, ElementId sourcePatternId)
    {
        if (sourcePatternId is null || sourcePatternId == ElementId.InvalidElementId)
            return ElementId.InvalidElementId;

        var sourcePattern = sourceDoc.GetElement(sourcePatternId) as FillPatternElement;
        if (sourcePattern is null) return ElementId.InvalidElementId;

        using var collector = new FilteredElementCollector(activeDoc).OfClass(typeof(FillPatternElement));
        var target = collector.Cast<FillPatternElement>()
            .FirstOrDefault(p => string.Equals(p.Name, sourcePattern.Name, StringComparison.OrdinalIgnoreCase));
        return target?.Id;
    }

    private static void SyncAppearance(Material source, Material target)
    {
        try
        {
            var sourceAssetId = source.AppearanceAssetId;
            if (sourceAssetId is null || sourceAssetId == ElementId.InvalidElementId) return;

            var sourceAssetElem = source.Document.GetElement(sourceAssetId) as AppearanceAssetElement;
            var diffuse = sourceAssetElem?.GetRenderingAsset()
                .FindByName("generic_diffuse") as AssetPropertyDoubleArray4d;
            var color = diffuse?.GetValueAsColor();
            if (color is null) return;

            var targetAssetElem = EnsureExclusiveAppearanceAsset(target);
            if (targetAssetElem is null) return;

            var doc = target.Document;
            using (var editScope = new AppearanceAssetEditScope(doc))
            {
                var editable = editScope.Start(targetAssetElem.Id);
                if (editable.FindByName("generic_diffuse") is AssetPropertyDoubleArray4d targetDiffuse)
                {
                    // Textures are never created from scratch: when the target
                    // slot carries a connected bitmap we keep it (best effort,
                    // platform limitation) — only the plain color is synced.
                    if (targetDiffuse.GetSingleConnectedAsset() is null)
                    {
                        targetDiffuse.SetValueAsColor(color);
                    }
                }
                // Commit(false): material syncs run in batches — no immediate
                // view redraw per material (official guidance for loops).
                editScope.Commit(false);
            }
        }
        catch (Exception ex)
        {
            LogSkip("Appearance", ex);
        }
    }

    /// <summary>
    /// Returns an appearance asset element that belongs to this material
    /// alone: duplicates the shared asset when other materials reference it,
    /// or duplicates any existing asset when the material has none
    /// (API-created materials).
    /// </summary>
    private static AppearanceAssetElement? EnsureExclusiveAppearanceAsset(Material target)
    {
        var doc = target.Document;
        var assetId = target.AppearanceAssetId;

        if (assetId is not null && assetId != ElementId.InvalidElementId)
        {
            var shared = CountMaterialsWithAsset(doc, assetId) > 1;
            if (!shared)
            {
                return doc.GetElement(assetId) as AppearanceAssetElement;
            }

            var current = doc.GetElement(assetId) as AppearanceAssetElement;
            var duplicate = DuplicateAsset(doc, current, target.Name);
            if (duplicate is not null)
            {
                target.AppearanceAssetId = duplicate.Id;
            }
            return duplicate;
        }

        using var collector = new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement));
        var prototype = collector.Cast<AppearanceAssetElement>().FirstOrDefault();
        var created = DuplicateAsset(doc, prototype, target.Name);
        if (created is not null)
        {
            target.AppearanceAssetId = created.Id;
        }
        return created;
    }

    private static AppearanceAssetElement? DuplicateAsset(
        Document doc, AppearanceAssetElement? source, string baseName)
    {
        if (source is null) return null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var name = attempt == 0 ? baseName : $"{baseName} ({attempt})";
            try
            {
                return source.Duplicate(name);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // name taken — try the next suffix
            }
        }
        return null;
    }

    private static int CountMaterialsWithAsset(Document doc, ElementId assetId)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Material));
        var count = 0;
        foreach (var m in collector.Cast<Material>())
        {
            if (m.AppearanceAssetId == assetId) count++;
        }
        return count;
    }

    private static void SyncStructuralAsset(Material source, Material target)
    {
        try
        {
            if (source.StructuralAssetId is null || source.StructuralAssetId == ElementId.InvalidElementId)
                return;
            if (source.Document.GetElement(source.StructuralAssetId) is not PropertySetElement sourcePse)
                return;
            var asset = sourcePse.GetStructuralAsset();

            var targetPse = target.StructuralAssetId is not null &&
                            target.StructuralAssetId != ElementId.InvalidElementId
                ? target.Document.GetElement(target.StructuralAssetId) as PropertySetElement
                : null;

            var exclusive = targetPse is not null &&
                CountMaterialsWithStructuralAsset(target.Document, target.StructuralAssetId!) <= 1;
            if (exclusive && targetPse!.GetStructuralAsset().StructuralAssetClass == asset.StructuralAssetClass)
            {
                targetPse.SetStructuralAsset(asset);
            }
            else
            {
                var created = PropertySetElement.Create(target.Document, asset);
                target.StructuralAssetId = created.Id;
            }
        }
        catch (Exception ex)
        {
            LogSkip("StructuralAsset", ex);
        }
    }

    private static void SyncThermalAsset(Material source, Material target)
    {
        try
        {
            if (source.ThermalAssetId is null || source.ThermalAssetId == ElementId.InvalidElementId)
                return;
            if (source.Document.GetElement(source.ThermalAssetId) is not PropertySetElement sourcePse)
                return;
            var asset = sourcePse.GetThermalAsset();

            var targetPse = target.ThermalAssetId is not null &&
                            target.ThermalAssetId != ElementId.InvalidElementId
                ? target.Document.GetElement(target.ThermalAssetId) as PropertySetElement
                : null;

            var exclusive = targetPse is not null &&
                CountMaterialsWithThermalAsset(target.Document, target.ThermalAssetId!) <= 1;
            if (exclusive && targetPse!.GetThermalAsset().ThermalMaterialType == asset.ThermalMaterialType)
            {
                targetPse.SetThermalAsset(asset);
            }
            else
            {
                var created = PropertySetElement.Create(target.Document, asset);
                target.ThermalAssetId = created.Id;
            }
        }
        catch (Exception ex)
        {
            LogSkip("ThermalAsset", ex);
        }
    }

    private static int CountMaterialsWithStructuralAsset(Document doc, ElementId assetId)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Material));
        var count = 0;
        foreach (var m in collector.Cast<Material>())
        {
            if (m.StructuralAssetId == assetId) count++;
        }
        return count;
    }

    private static int CountMaterialsWithThermalAsset(Document doc, ElementId assetId)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Material));
        var count = 0;
        foreach (var m in collector.Cast<Material>())
        {
            if (m.ThermalAssetId == assetId) count++;
        }
        return count;
    }

    private static void LogSkip(string what, Exception ex)
    {
        SmartConLogger.Debug($"Material sync: {what} skipped — {ex.Message}");
    }
}
