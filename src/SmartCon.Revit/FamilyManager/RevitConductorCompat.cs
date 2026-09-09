#if REVIT2026_OR_GREATER
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using SmartCon.Core.Logging;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit 2026+ Conductor* model seam (Issue #233). Revit 2026 replaced the
/// <c>ElectricalSetting</c> wire-settings object graph (WireMaterialType →
/// TemperatureRatingType → InsulationType/WireSize) with four FLAT
/// document-level lists: <see cref="ConductorMaterial"/>,
/// <see cref="TemperatureRating"/>, <see cref="InsulationMaterial"/>,
/// <see cref="ConductorSize"/>. The wrappers are plain data objects
/// (<c>IDisposable</c>, NOT Element-derived): <c>doc.GetElement</c> and
/// collectors do not work for them — every access goes through the per-kind
/// statics taking the <see cref="Document"/>. <c>WireType.WireMaterial/
/// TemperatureRating/Insulation</c> became <see cref="ElementId"/> and
/// <c>MaxSize</c> became the conductor-size NAME (string). Verified
/// identical on Revit 2027 (revitapidocs 2026 ≡ 2027).
/// <para>
/// Read helpers degrade to <c>null</c> on any failure (foreign id,
/// failure-mode document) exactly like the ≤2025 null-object case; write
/// helpers (<c>ResolveOrCreate*</c>/<see cref="EnsureConductorSize"/>)
/// require an open transaction on a PROJECT document (Revit throws
/// <c>ModificationOutsideTransactionException</c>/<c>ArgumentException</c>
/// otherwise) and let exceptions bubble to the caller's Warn+NotConverged
/// handler (<c>TrySetWireMember</c>). Name lookup mirrors the ≤2025
/// <c>FindByName</c> semantics: exact match first, then an
/// ordinal-ignore-case scan, then creation.
/// </para>
/// </summary>
internal static class RevitConductorCompat
{
    public static string? MaterialName(Document doc, ElementId? id)
    {
        if (!IsValidId(id)) return null;
        try
        {
            using var material = ConductorMaterial.GetConductorMaterial(doc, id);
            return material is { IsValidObject: true } ? material.Name : null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Conductor material read failed for id {id!.Value}: {ex.Message}");
            return null;
        }
    }

    public static string? TemperatureRatingName(Document doc, ElementId? id)
    {
        if (!IsValidId(id)) return null;
        try
        {
            using var rating = TemperatureRating.GetTemperatureRating(doc, id);
            return rating is { IsValidObject: true } ? rating.Name : null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Temperature rating read failed for id {id!.Value}: {ex.Message}");
            return null;
        }
    }

    public static string? InsulationName(Document doc, ElementId? id)
    {
        if (!IsValidId(id)) return null;
        try
        {
            using var insulation = InsulationMaterial.GetInsulationMaterial(doc, id);
            return insulation is { IsValidObject: true } ? insulation.Name : null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Insulation material read failed for id {id!.Value}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Diameter (internal units) of the conductor size with the given name,
    /// or <c>null</c> when the size does not exist / cannot be read. Used to
    /// carry the numeric content across documents when the target project
    /// lacks the size (the snapshot holds names only).
    /// </summary>
    public static double? SizeDiameter(Document doc, string? name)
    {
        var id = FindSizeIdByName(doc, name);
        if (!IsValidId(id)) return null;
        try
        {
            using var size = ConductorSize.GetConductorSize(doc, id);
            return size is { IsValidObject: true } ? size.Diameter : null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Conductor size '{name}' read failed: {ex.Message}");
            return null;
        }
    }

    public static ElementId ResolveOrCreateMaterial(Document doc, string name) =>
        ResolveOrCreate(
            doc, name, "material",
            static (d, n) => SafeFindId(() => ConductorMaterial.GetConductorMaterialIdByName(d, n)),
            static d => ConductorMaterial.GetConductorMaterialIds(d),
            MaterialName,
            static (d, n) =>
            {
                using var created = ConductorMaterial.Create(d);
                created.Name = n;
                return created.Id;
            });

    public static ElementId ResolveOrCreateTemperatureRating(Document doc, string name) =>
        ResolveOrCreate(
            doc, name, "temperature rating",
            static (d, n) => SafeFindId(() => TemperatureRating.GetTemperatureRatingIdByName(d, n)),
            static d => TemperatureRating.GetTemperatureRatingIds(d),
            TemperatureRatingName,
            static (d, n) =>
            {
                using var created = TemperatureRating.Create(d);
                created.Name = n;
                return created.Id;
            });

    public static ElementId ResolveOrCreateInsulation(Document doc, string name) =>
        ResolveOrCreate(
            doc, name, "insulation material",
            static (d, n) => SafeFindId(() => InsulationMaterial.GetInsulationMaterialIdByName(d, n)),
            static d => InsulationMaterial.GetInsulationMaterialIds(d),
            InsulationName,
            static (d, n) =>
            {
                using var created = InsulationMaterial.Create(d);
                created.Name = n;
                return created.Id;
            });

    /// <summary>
    /// Ensures a conductor size with the given name exists in the target
    /// document, creating it with the source diameter when missing —
    /// <c>WireType.MaxSize</c> only accepts an EXISTING size name (or empty),
    /// so the size must exist before the assignment.
    /// </summary>
    public static void EnsureConductorSize(Document doc, string name, double? diameter)
    {
        var id = FindSizeIdByName(doc, name);
        if (IsValidId(id)) return;

        id = ScanIdsByName(
            doc,
            static d => ConductorSize.GetConductorSizeIds(d),
            static (d, sizeId) =>
            {
                if (!IsValidId(sizeId)) return null;
                try
                {
                    using var size = ConductorSize.GetConductorSize(d, sizeId);
                    return size is { IsValidObject: true } ? size.Name : null;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Conductor size read failed for id {sizeId!.Value}: {ex.Message}");
                    return null;
                }
            },
            name);
        if (IsValidId(id)) return;

        using var created = ConductorSize.Create(doc);
        created.Name = name;
        if (diameter is { } d && d > 0 && double.IsFinite(d))
            created.Diameter = d;
        SmartConLogger.Info(
            $"Conductor size '{name}' missing in the project — created" +
            (diameter is null ? " (diameter unknown — set it in the electrical settings if it matters)." : "."));
    }

    /// <summary>All conductor-object ids of every kind — the finder guard (#233):
    /// a cheap insurance that none of them can ever surface in an ElementType
    /// collector as a phantom system type (the ≤2025 WireMaterialType did,
    /// manual test 2026-08-04; the 2026 internals are undocumented, so the
    /// guard is defensive by design).</summary>
    public static HashSet<ElementId> CollectAllConductorIds(Document doc)
    {
        var ids = new HashSet<ElementId>();
        AddAll(ids, static d => ConductorMaterial.GetConductorMaterialIds(d), doc);
        AddAll(ids, static d => TemperatureRating.GetTemperatureRatingIds(d), doc);
        AddAll(ids, static d => InsulationMaterial.GetInsulationMaterialIds(d), doc);
        AddAll(ids, static d => ConductorSize.GetConductorSizeIds(d), doc);
        return ids;

        static void AddAll(HashSet<ElementId> set, Func<Document, IList<ElementId>> getAll, Document d)
        {
            try
            {
                foreach (var id in getAll(d))
                    set.Add(id);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"Conductor id enumeration failed: {ex.Message}");
            }
        }
    }

    private static ElementId ResolveOrCreate(
        Document doc,
        string name,
        string kind,
        Func<Document, string, ElementId> findByName,
        Func<Document, IList<ElementId>> getAllIds,
        Func<Document, ElementId?, string?> nameOf,
        Func<Document, string, ElementId> create)
    {
        var id = findByName(doc, name);
        if (IsValidId(id)) return id;

        id = ScanIdsByName(doc, getAllIds, nameOf, name);
        if (IsValidId(id)) return id;

        var createdId = create(doc, name);
        SmartConLogger.Info(
            $"Conductor {kind} '{name}' missing in the project — created (#233); " +
            "its numeric content follows the Revit defaults — adjust in Manage > MEP Settings > " +
            "Electrical Conductor and Cable Settings if it differs.");
        return createdId;
    }

    private static ElementId ScanIdsByName(
        Document doc,
        Func<Document, IList<ElementId>> getAllIds,
        Func<Document, ElementId?, string?> nameOf,
        string name)
    {
        try
        {
            foreach (var id in getAllIds(doc))
            {
                if (string.Equals(nameOf(doc, id), name, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Conductor enumeration failed while looking for '{name}': {ex.Message}");
        }
        return ElementId.InvalidElementId;
    }

    private static ElementId FindSizeIdByName(Document doc, string? name)
    {
        if (string.IsNullOrEmpty(name)) return ElementId.InvalidElementId;
        return SafeFindId(() => ConductorSize.GetConductorSizeIdByName(doc, name));
    }

    /// <summary>
    /// The by-name statics' not-found behaviour is undocumented (invalid id
    /// vs throw) — both are treated as "not found".
    /// </summary>
    private static ElementId SafeFindId(Func<ElementId> find)
    {
        try
        {
            return find() ?? ElementId.InvalidElementId;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Conductor find-by-name failed: {ex.Message}");
            return ElementId.InvalidElementId;
        }
    }

    private static bool IsValidId(ElementId? id) =>
        id is not null && id != ElementId.InvalidElementId;
}
#endif
