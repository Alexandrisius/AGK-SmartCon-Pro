using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    public IReadOnlyList<FamilyGeometryPerType> ExtractGeometryPerType(
        Document familyDoc,
        CancellationToken ct = default)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(familyDoc);
#else
        if (familyDoc is null) throw new ArgumentNullException(nameof(familyDoc));
#endif

        using var _scope = SmartConLogger.BeginScope("Geo3DPerType",
            ("Method", nameof(ExtractGeometryPerType)));

        var result = new List<FamilyGeometryPerType>();
        var familyName = familyDoc.Title;
        if (familyName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            familyName = familyName[..^4];

        // View-specific (annotation/detail) families never display in 3D
        // views — skip before the expensive per-type loop (type switch +
        // implicit regeneration, ~3 s/type on a title block with 13 nested
        // annotation instances). Callers treat the empty result as "no
        // extractable geometry": the pipeline writes the terminal
        // glb_state=-1 marker (#157), so detection never stays pending.
        var rootCategory = familyDoc.OwnerFamily?.FamilyCategory;
        if (RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(rootCategory))
        {
            SmartConLogger.Info(
                $"ExtractGeometryPerType: '{familyName}' is a view-specific family " +
                $"(category '{rootCategory!.Name}') — 3D preview not applicable, extraction skipped");
            return result;
        }

        var fm = familyDoc.FamilyManager;
        var typeCount = fm.Types.Size;

        SmartConLogger.Info($"ExtractGeometryPerType: familyName='{familyName}', typeCount={typeCount}");

        if (typeCount <= 1)
        {
            ct.ThrowIfCancellationRequested();
            var typeName = fm.CurrentType?.Name ?? "";
            if (string.IsNullOrWhiteSpace(typeName))
            {
                // Family with no types created (typeCount=0) OR a single unnamed
                // default type (typeCount=1, Name=""). This is a normal family
                // where the user didn't create explicit types — the geometry
                // lives in the family document itself, not in any type.
                // Jeremy Tammik (The Building Coder): "A family loaded into a
                // project that has no types in it will get a default type
                // assigned to it in the project editor that's the same as the
                // short family file name." We mirror that here: use familyName
                // as the type name so the viewer and tree show it consistently.
                // Previously this returned an empty result, skipping geometry
                // extraction entirely — the 3D viewer showed nothing.
                typeName = familyName;
                SmartConLogger.Info(
                    $"ExtractGeometryPerType: no named type — using family name '{familyName}' as type name");
            }

            var extraction = RevitFamilyGeometryExtractor.ExtractMeshesFromFamilyDoc(familyDoc, ct);
            result.Add(new FamilyGeometryPerType(
                typeName, familyName, extraction.Meshes,
                new PreviewTypeSnapshot(typeName, extraction.PreviewForms, extraction.PreviewNestedInstances)));
            SmartConLogger.Info(
                $"ExtractGeometryPerType: single type '{typeName}' → {extraction.Meshes.Count} meshes, " +
                $"{(extraction.Meshes.Count > 0 ? extraction.Meshes.Sum(m => m.TriangleCount) : 0)} triangles");
            return result;
        }

        // Multiple types: iterate with Transaction+RollBack (I-03b).
        // Precedent: RevitFamilyDataExtractionService.cs:227 uses the
        // same pattern for temp type creation in family documents.
        FamilyType? originalType = null;
        try { originalType = fm.CurrentType; }
        catch { }

        using (var tx = new Transaction(familyDoc, "SmartCon_GeometryPerType"))
        {
            tx.Start();
            SmartConLogger.Debug(
                "Geo3DPerType: Transaction.Started, familyDoc.IsModified=" + familyDoc.IsModified
                + ", IsReadOnly=" + familyDoc.IsReadOnly
                + ", IsValidObject=" + familyDoc.IsValidObject);
            try
            {
                foreach (FamilyType ft in fm.Types)
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(ft.Name))
                    {
                        SmartConLogger.Debug("ExtractGeometryPerType: skipping unnamed default type");
                        continue;
                    }

                    try
                    {
                        var typeBefore = fm.CurrentType?.Name ?? "<none>";
                        fm.CurrentType = ft;
                        var typeAfter = fm.CurrentType?.Name ?? "<none>";
                        SmartConLogger.Debug(
                            $"ExtractGeometryPerType: switched type '{ft.Name}' (before='{typeBefore}', after='{typeAfter}')");
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn(
                            $"ExtractGeometryPerType: failed to switch to type '{ft.Name}': {ex.Message} " +
                            "[Action: type geometry will be skipped]");
                        continue;
                    }

                    // NOTE: Regenerate() removed — Jeremy Tammik (Autodesk):
                    // "Whenever a transaction is committed, Revit regenerates
                    // the document for you anyway." Inside an uncommitted
                    // transaction, calling Regenerate on a held-open family
                    // document triggers Revit's view-update machinery, which
                    // on net48 (Revit 2019-2024) can leave the WPF render
                    // thread in a zombie state — the next ShowDialog then
                    // blocks ~9 seconds waiting for the render thread to pump
                    // paint messages (intermittent, depending on whether
                    // layout completed before the held-open document state
                    // was modified). Switching fm.CurrentType already updates
                    // the in-memory family model; get_Geometry sees the new
                    // type's parameter values without explicit Regenerate.
                    // See: docs/adr/042-familymanager-3d-preview.md (net48
                    // white-dialog bug).

                    var extraction = RevitFamilyGeometryExtractor.ExtractMeshesFromFamilyDoc(familyDoc, ct);

                    if (extraction.Meshes.Count > 0 && !extraction.Meshes.All(m => m.IsEmpty))
                    {
                        var triCount = extraction.Meshes.Sum(m => m.TriangleCount);
                        result.Add(new FamilyGeometryPerType(
                            ft.Name, familyName, extraction.Meshes,
                            new PreviewTypeSnapshot(ft.Name, extraction.PreviewForms, extraction.PreviewNestedInstances)));
                        SmartConLogger.Info(
                            $"ExtractGeometryPerType: type '{ft.Name}' → {extraction.Meshes.Count} meshes, {triCount} triangles");
                    }
                    else
                    {
                        SmartConLogger.Info(
                            $"ExtractGeometryPerType: type '{ft.Name}' → no geometry extracted");
                    }
                }
            }
            finally
            {
                if (originalType is not null)
                {
                    try { fm.CurrentType = originalType; }
                    catch { }
                }
                SmartConLogger.Debug(
                    "Geo3DPerType: before RollBack, familyDoc.IsModified=" + familyDoc.IsModified);
                tx.RollBack();
                SmartConLogger.Debug(
                    "Geo3DPerType: after RollBack, familyDoc.IsModified=" + familyDoc.IsModified
                    + ", IsReadOnly=" + familyDoc.IsReadOnly);
            }
        }

        SmartConLogger.Info(
            $"ExtractGeometryPerType: extracted {result.Count}/{typeCount} types with geometry for '{familyName}'");

        if (result.Count == 0)
        {
            SmartConLogger.Warn(
                $"ExtractGeometryPerType: no geometry for any type in '{familyName}' (typeCount={typeCount}) " +
                "[Action: verify family has visible 3D solids; 3D preview will be unavailable]");
        }

        return result;
    }
}
