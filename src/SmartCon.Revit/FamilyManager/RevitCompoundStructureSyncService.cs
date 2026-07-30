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
                materialId));
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

            var structuralIndex = IndexOf(
                structure.Layers, l => l.Function == (int)MaterialFunctionAssignment.Structure);
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
