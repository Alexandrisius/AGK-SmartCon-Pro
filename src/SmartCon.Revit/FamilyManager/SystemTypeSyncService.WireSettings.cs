using System.Collections;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Electrical = Autodesk.Revit.DB.Electrical;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class SystemTypeSyncService
{
    /// <summary>
    /// FHV5: sync the wire settings — material / temperature rating /
    /// insulation / max size / conduit / neutral scalars. On Revit ≤2025 the
    /// members are an ownership-chain object graph (revitapidocs): temperature
    /// ratings belong to the assigned material; insulations and wire sizes
    /// belong to the assigned rating. A missing material is created from any
    /// existing one (same as "Duplicate" in the Revit electrical settings
    /// UI); missing rating/insulation/size/conduit objects are NOT created
    /// (their numeric content — ampacity, diameter — cannot be invented)
    /// and count as NotConverged with a Warn. Revit 2026+ replaced the graph
    /// with the FLAT Conductor* model (#233, <see cref="RevitConductorCompat"/>):
    /// every conductor kind is an independent document-level list with a
    /// Create() factory, so ALL missing conductor members (material/rating/
    /// insulation/size — the size carrying the source diameter) are created
    /// by name and the sync fully converges; only the conduit (still
    /// WireConduitType, no creation API) keeps the find-only degradation.
    /// </summary>
    private int SyncWireSettings(Document doc, Electrical.WireType source, Electrical.WireType target)
    {
        var notConverged = 0;

#if REVIT2026_OR_GREATER
        // #233: flat Conductor* model — WireMaterial/TemperatureRating/
        // Insulation are ElementIds into document-level lists, MaxSize is
        // the conductor-size NAME. Names are read through the per-kind
        // statics (the objects are not Element-derived); the source names
        // come from the SOURCE document, resolution/creation happens in the
        // target. Empty/invalid source members are no-ops (≤2025 semantics).
        notConverged += TrySetWireMember("WireMaterial", () =>
        {
            var sourceName = RevitConductorCompat.MaterialName(source.Document, source.WireMaterial);
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(
                    RevitConductorCompat.MaterialName(doc, target.WireMaterial),
                    sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            target.WireMaterial = RevitConductorCompat.ResolveOrCreateMaterial(doc, sourceName!);
        });

        notConverged += TrySetWireMember("TemperatureRating", () =>
        {
            var sourceName = RevitConductorCompat.TemperatureRatingName(source.Document, source.TemperatureRating);
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(
                    RevitConductorCompat.TemperatureRatingName(doc, target.TemperatureRating),
                    sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            target.TemperatureRating = RevitConductorCompat.ResolveOrCreateTemperatureRating(doc, sourceName!);
        });

        notConverged += TrySetWireMember("Insulation", () =>
        {
            var sourceName = RevitConductorCompat.InsulationName(source.Document, source.Insulation);
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(
                    RevitConductorCompat.InsulationName(doc, target.Insulation),
                    sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            target.Insulation = RevitConductorCompat.ResolveOrCreateInsulation(doc, sourceName!);
        });

        notConverged += TrySetWireMember("MaxSize", () =>
        {
            var sourceName = source.MaxSize;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.MaxSize, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            // WireType.MaxSize accepts only an EXISTING conductor-size name —
            // ensure the size first, carrying the source diameter (the
            // snapshot holds names only; the numeric content lives here).
            RevitConductorCompat.EnsureConductorSize(
                doc, sourceName!, RevitConductorCompat.SizeDiameter(source.Document, sourceName));
            target.MaxSize = sourceName;
        });
#else
        notConverged += TrySetWireMember("WireMaterial", () =>
        {
            var sourceName = source.WireMaterial?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.WireMaterial?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var material = FindByName<Electrical.WireMaterialType>(
                doc.Settings.ElectricalSetting?.WireMaterialTypes, sourceName!)
                ?? CreateWireMaterial(doc, sourceName!);
            if (material is null)
            {
                throw new InvalidOperationException(
                    $"wire material '{sourceName}' not found in the project and no base material exists to duplicate");
            }
            target.WireMaterial = material;
        });

        notConverged += TrySetWireMember("TemperatureRating", () =>
        {
            var sourceName = source.TemperatureRating?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.TemperatureRating?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var rating = target.WireMaterial?.TemperatureRatings
                ?.Cast<Electrical.TemperatureRatingType>()
                .FirstOrDefault(r => string.Equals(r.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (rating is null)
            {
                throw new InvalidOperationException(
                    $"temperature rating '{sourceName}' not found under material '{target.WireMaterial?.Name}' in the project");
            }
            target.TemperatureRating = rating;
        });

        notConverged += TrySetWireMember("Insulation", () =>
        {
            var sourceName = source.Insulation?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.Insulation?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var insulation = target.TemperatureRating?.InsulationTypes
                ?.Cast<Electrical.InsulationType>()
                .FirstOrDefault(i => string.Equals(i.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (insulation is null)
            {
                throw new InvalidOperationException(
                    $"insulation '{sourceName}' not found under temperature rating '{target.TemperatureRating?.Name}' in the project");
            }
            target.Insulation = insulation;
        });

        notConverged += TrySetWireMember("MaxSize", () =>
        {
            var sourceName = source.MaxSize?.Size;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.MaxSize?.Size, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var size = target.TemperatureRating?.WireSizes
                ?.Cast<Electrical.WireSize>()
                .FirstOrDefault(s => string.Equals(s.Size, sourceName, StringComparison.OrdinalIgnoreCase));
            if (size is null)
            {
                throw new InvalidOperationException(
                    $"wire size '{sourceName}' not found under temperature rating '{target.TemperatureRating?.Name}' in the project");
            }
            target.MaxSize = size;
        });
#endif

        notConverged += TrySetWireMember("Conduit", () =>
        {
            var sourceName = source.Conduit?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.Conduit?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var conduit = FindByName<Electrical.WireConduitType>(
                doc.Settings.ElectricalSetting?.WireConduitTypes, sourceName!);
            if (conduit is null)
            {
                throw new InvalidOperationException(
                    $"wire conduit '{sourceName}' not found in the project electrical settings");
            }
            target.Conduit = conduit;
        });

        notConverged += TrySetWireMember("NeutralMultiplier",
            () => target.NeutralMultiplier = source.NeutralMultiplier);
        notConverged += TrySetWireMember("NeutralRequired",
            () => target.NeutralRequired = source.NeutralRequired);

        return notConverged;
    }

    private static int TrySetWireMember(string memberName, Action write)
    {
        try
        {
            write();
            return 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Wire settings member '{memberName}' rejected: {ex.Message} " +
                "[Action: check the member in the project electrical settings — Manage > MEP Settings > " +
                "Electrical Conductor and Cable Settings (Revit 2026+) or Electrical Settings > Wiring (≤2025) — " +
                "and re-run the sync; the remaining members were applied]");
            return 1;
        }
    }

    private static T? FindByName<T>(IEnumerable? set, string name) where T : class
    {
        if (set is null) return null;
        foreach (var item in set)
        {
            if (item is not T typed) continue;
            var itemName = typed switch
            {
                Element element => element.Name,
                Electrical.WireConduitType conduit => conduit.Name,
                _ => null,
            };
            if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
                return typed;
        }
        return null;
    }

#if !REVIT2026_OR_GREATER
    private static Electrical.WireMaterialType? CreateWireMaterial(Document doc, string name)
    {
        var setting = doc.Settings.ElectricalSetting;
        if (setting is null) return null;
        var baseMaterial = setting.WireMaterialTypes?.Cast<Electrical.WireMaterialType>().FirstOrDefault();
        if (baseMaterial is null) return null;
        SmartConLogger.Info(
            $"Wire material '{name}' missing in the project — created by duplicating '{baseMaterial.Name}' " +
            "(impedance factors follow the base; adjust in the electrical settings if they differ).");
        return setting.AddWireMaterialType(name, baseMaterial);
    }
#endif
}
