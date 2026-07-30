using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="ICompoundStructureSyncService"/>
/// (Issue #104). Builds a brand-new <see cref="CompoundStructure"/> from the
/// reference snapshot (layers in reference order, shell counts, variable
/// layer, structural layer) and applies it via
/// <c>HostObjAttributes.SetCompoundStructure</c>. A rejected structure
/// (Revit validation) leaves the target type untouched.
/// </summary>
/// <remarks>
/// Known limitation: vertically compound walls are not synchronized — the
/// snapshot extractor cannot read their layer stack (Revit API throws) and
/// reports <c>Structure = null</c>, which is also the canonical state of
/// stacked/curtain walls. Such types keep their existing structure; only
/// their parameters are synced.
/// </remarks>
public sealed class RevitCompoundStructureSyncService : ICompoundStructureSyncService
{
    private readonly IMaterialSyncService _materialSync;

    public RevitCompoundStructureSyncService(IMaterialSyncService materialSync)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(materialSync);
#else
        if (materialSync is null) throw new ArgumentNullException(nameof(materialSync));
#endif
        _materialSync = materialSync;
    }

    public int SyncStructure(
        Document sourceDoc,
        Document activeDoc,
        ElementType target,
        CompoundStructureSnapshot structure)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(sourceDoc);
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(structure);
#else
        if (sourceDoc is null) throw new ArgumentNullException(nameof(sourceDoc));
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (structure is null) throw new ArgumentNullException(nameof(structure));
#endif

        if (target is not HostObjAttributes hostType)
        {
            SmartConLogger.Warn(
                $"Type '{target.Name}': reference has a compound structure but the project type " +
                "is not a host type. [Action: structure sync skipped — check the category mapping]");
            return 1;
        }

        if (structure.Layers.Count == 0)
        {
            SmartConLogger.Warn(
                $"Type '{target.Name}': reference compound structure has no layers. " +
                "[Action: fix the type in the mini-project and reimport it]");
            return 1;
        }

        using var _scope = SmartConLogger.BeginScope(
            "SystemSync",
            ("Method", nameof(SyncStructure)),
            ("TypeName", target.Name));

        var notConverged = 0;

        var layers = new List<CompoundStructureLayer>(structure.Layers.Count);
        foreach (var layerSnapshot in structure.Layers)
        {
            var materialId = ElementId.InvalidElementId;
            if (layerSnapshot.MaterialName is not null)
            {
                materialId = _materialSync.SyncMaterial(sourceDoc, activeDoc, layerSnapshot.MaterialName)
                    ?? ElementId.InvalidElementId;
                if (materialId == ElementId.InvalidElementId)
                {
                    notConverged++;
                    SmartConLogger.Warn(
                        $"Type '{target.Name}': layer material '{layerSnapshot.MaterialName}' " +
                        "could not be resolved; the layer gets '<By Category>'. " +
                        "[Action: check the material warnings above, then re-run the sync]");
                }
            }

            layers.Add(new CompoundStructureLayer(
                layerSnapshot.Width,
                (MaterialFunctionAssignment)layerSnapshot.Function,
                materialId)
            {
                LayerCapFlag = layerSnapshot.LayerCapFlag,
            });
        }

        CompoundStructure compoundStructure;
        try
        {
            compoundStructure = CompoundStructure.CreateSimpleCompoundStructure(layers);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Type '{target.Name}': reference structure is invalid ({ex.Message}); " +
                "the existing structure is kept. " +
                "[Action: fix the type in the mini-project and reimport it]");
            return notConverged + 1;
        }

        using (compoundStructure)
        {
            // Shell counts move the leading/trailing layers out of the core —
            // must be set AFTER the layer list (CreateSimple puts everything
            // into the core). There must always be at least one core layer.
            try
            {
                compoundStructure.SetNumberOfShellLayers(
                    ShellLayerType.Exterior, structure.ExteriorShellLayerCount);
                compoundStructure.SetNumberOfShellLayers(
                    ShellLayerType.Interior, structure.InteriorShellLayerCount);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Type '{target.Name}': shell layer counts are inconsistent with the layer " +
                    $"list ({ex.Message}); the existing structure is kept. " +
                    "[Action: fix the type in the mini-project and reimport it]");
                return notConverged + 1;
            }

            var variableIndex = IndexOf(structure.Layers, l => l.IsVariable);
            if (variableIndex >= 0)
            {
                try { compoundStructure.VariableLayerIndex = variableIndex; }
                catch (Exception ex)
                {
                    notConverged++;
                    SmartConLogger.Warn(
                        $"Type '{target.Name}': variable layer index rejected ({ex.Message}). " +
                        "[Action: check the layer stack in the mini-project, then re-run the sync]");
                }
            }

            // The exact structural layer ("Материал несущих конструкций"
            // checkbox) from the reference; fallback — first Structure-function
            // layer (legacy snapshots without the index).
            var structuralIndex = structure.StructuralMaterialIndex >= 0
                ? structure.StructuralMaterialIndex
                : IndexOf(structure.Layers, l => l.Function == (int)MaterialFunctionAssignment.Structure);
            if (structuralIndex >= 0)
            {
                try { compoundStructure.StructuralMaterialIndex = structuralIndex; }
                catch (Exception ex)
                {
                    notConverged++;
                    SmartConLogger.Warn(
                        $"Type '{target.Name}': structural material index rejected ({ex.Message}). " +
                        "[Action: check the layer stack in the mini-project, then re-run the sync]");
                }
            }

            // EndCap ("Огибание в торцах стен") and OpeningWrapping
            // ("Огибание в местах вставки элементов") from the reference.
            // Fallback for legacy snapshots without the values: walls keep
            // the built default, floors/roofs/ceilings require NoEndCap
            // (Revit validation: "wrong EndCap condition").
            try
            {
                if (structure.EndCap >= 0)
                {
                    compoundStructure.EndCap = (EndCapCondition)structure.EndCap;
                }
                else if (target is not WallType)
                {
                    compoundStructure.EndCap = EndCapCondition.NoEndCap;
                }
            }
            catch (Exception ex) { SmartConLogger.Debug($"EndCap skipped: {ex.Message}"); }
            try
            {
                if (structure.OpeningWrapping >= 0)
                {
                    compoundStructure.OpeningWrapping = (OpeningWrappingCondition)structure.OpeningWrapping;
                }
            }
            catch (Exception ex) { SmartConLogger.Debug($"OpeningWrapping skipped: {ex.Message}"); }

            // Per-layer wrapping participation — only for shell layers
            // (LayerCapFlag is set on the layer objects at construction).
            for (var i = 0; i < structure.Layers.Count; i++)
            {
                var isShellLayer = i < structure.ExteriorShellLayerCount ||
                    i >= structure.Layers.Count - structure.InteriorShellLayerCount;
                if (isShellLayer)
                {
                    try { compoundStructure.SetParticipatesInWrapping(i, structure.Layers[i].ParticipatesInWrapping); }
                    catch (Exception ex) { SmartConLogger.Debug($"SetParticipatesInWrapping({i}) skipped: {ex.Message}"); }
                }
            }

            try
            {
                hostType.SetCompoundStructure(compoundStructure);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Type '{target.Name}': SetCompoundStructure rejected ({ex.Message}); " +
                    "the existing structure is kept. " +
                    "[Action: fix the type in the mini-project and reimport it]");
                return notConverged + 1;
            }
        }

        SmartConLogger.Info(
            $"Type '{target.Name}': compound structure applied " +
            $"({structure.Layers.Count} layers, shells {structure.ExteriorShellLayerCount}+" +
            $"{structure.InteriorShellLayerCount}).");
        return notConverged;
    }

    private static int IndexOf(
        IReadOnlyList<CompoundLayerSnapshot> layers,
        Func<CompoundLayerSnapshot, bool> predicate)
    {
        for (var i = 0; i < layers.Count; i++)
        {
            if (predicate(layers[i])) return i;
        }
        return -1;
    }
}
