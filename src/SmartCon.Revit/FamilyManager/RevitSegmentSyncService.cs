using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="ISegmentSyncService"/> (Issue #104).
/// Segments are matched by name. The size table converges to the reference:
/// add/correct always; remove only sizes not used by placed MEP curves
/// (Revit forbids deleting in-use content) and never the last size —
/// the residue is reported as not-converged. Creation is supported for pipe
/// segments (<c>PipeSegment.Create</c>); the Revit API does not expose duct
/// segment creation, so a missing duct segment is reported and its routing
/// rule is skipped by the caller.
/// </summary>
public sealed class RevitSegmentSyncService : ISegmentSyncService
{
    private const double DiameterTolerance = 1e-9;

    private readonly IMaterialSyncService _materialSync;

    public RevitSegmentSyncService(IMaterialSyncService materialSync)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(materialSync);
#else
        if (materialSync is null) throw new ArgumentNullException(nameof(materialSync));
#endif
        _materialSync = materialSync;
    }

    public SegmentSnapshot? ReadSegment(Document sourceDoc, string segmentName)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(sourceDoc);
        ArgumentNullException.ThrowIfNull(segmentName);
#else
        if (sourceDoc is null) throw new ArgumentNullException(nameof(sourceDoc));
        if (segmentName is null) throw new ArgumentNullException(nameof(segmentName));
#endif

        var segment = FindSegmentByName(sourceDoc, segmentName);
        if (segment is null) return null;

        // Single builder shared with the FHV4 hash extraction (ADR-065) —
        // the hash must reflect exactly what the sync writes.
        return RevitFamilySnapshotExtractor.BuildSegmentSnapshot(segment, sourceDoc);
    }

    public SegmentSyncResult SyncSegment(Document sourceDoc, Document activeDoc, string segmentName)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(sourceDoc);
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(segmentName);
#else
        if (sourceDoc is null) throw new ArgumentNullException(nameof(sourceDoc));
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (segmentName is null) throw new ArgumentNullException(nameof(segmentName));
#endif

        using var _scope = SmartConLogger.BeginScope(
            "SystemSync",
            ("Method", nameof(SyncSegment)),
            ("SegmentName", segmentName));

        var snapshot = ReadSegment(sourceDoc, segmentName);
        if (snapshot is null)
        {
            SmartConLogger.Warn(
                $"Segment '{segmentName}' not found in the source mini-project. " +
                "[Action: routing rule skipped; reimport the mini-project into the catalog]");
            return new SegmentSyncResult(null, 0, 0, 0);
        }

        var existing = FindSegmentByName(activeDoc, segmentName);
        if (existing is not null)
        {
            return SyncSizes(activeDoc, existing, snapshot);
        }

        return CreateSegment(sourceDoc, activeDoc, snapshot);
    }

    private SegmentSyncResult CreateSegment(Document sourceDoc, Document activeDoc, SegmentSnapshot snapshot)
    {
        if (snapshot.ScheduleName is null)
        {
            // Duct segment: the Revit API does not expose duct segment
            // creation (only PipeSegment.Create exists).
            SmartConLogger.Warn(
                $"Segment '{snapshot.Name}': missing in the project and cannot be created " +
                "(duct segment creation is not exposed by the Revit API). " +
                "[Action: create the segment in the project manually, then re-run the sync]");
            return new SegmentSyncResult(null, 0, 0, 0);
        }

        if (snapshot.Sizes.Count == 0)
        {
            SmartConLogger.Warn(
                $"Segment '{snapshot.Name}': reference has an empty size table; creation impossible. " +
                "[Action: fix the segment in the mini-project and reimport it]");
            return new SegmentSyncResult(null, 0, 0, 0);
        }

        ElementId? materialId = null;
        if (snapshot.MaterialName is not null)
        {
            materialId = _materialSync.SyncMaterial(sourceDoc, activeDoc, snapshot.MaterialName);
        }
        if (materialId is null)
        {
            SmartConLogger.Warn(
                $"Segment '{snapshot.Name}': material '{snapshot.MaterialName ?? "<none>"}' " +
                "could not be resolved in the project; creation impossible. " +
                "[Action: check the material in the mini-project, then re-run the sync]");
            return new SegmentSyncResult(null, 0, 0, 0);
        }

        var scheduleId = EnsurePipeSchedule(activeDoc, snapshot.ScheduleName);
        if (scheduleId is null)
        {
            SmartConLogger.Warn(
                $"Segment '{snapshot.Name}': pipe schedule '{snapshot.ScheduleName}' " +
                "could not be found or created. " +
                "[Action: create the pipe schedule in the project manually, then re-run the sync]");
            return new SegmentSyncResult(null, 0, 0, 0);
        }

        try
        {
            var sizes = snapshot.Sizes.Select(ToMEPSize).ToList();
            var created = PipeSegment.Create(activeDoc, materialId, scheduleId, sizes);
            if (!string.Equals(created.Name, snapshot.Name, StringComparison.Ordinal))
            {
                try { created.Name = snapshot.Name; } catch { /* name is informational */ }
            }
            try { created.Roughness = snapshot.Roughness; }
            catch { /* roughness is best-effort */ }
            SmartConLogger.Info(
                $"Segment '{snapshot.Name}': created with {sizes.Count} sizes.");
            return new SegmentSyncResult(created.Id, sizes.Count, 0, 0);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            // The (material, schedule) combination is already used by another
            // segment — Revit enforces combo uniqueness.
            SmartConLogger.Warn(
                $"Segment '{snapshot.Name}': creation refused — {ex.Message} " +
                "[Action: reconcile the project's segments manually (the material+schedule " +
                "combination is taken by another segment), then re-run the sync]");
            return new SegmentSyncResult(null, 0, 0, 0);
        }
    }

    private SegmentSyncResult SyncSizes(Document activeDoc, Segment segment, SegmentSnapshot snapshot)
    {
        var added = 0;
        var removed = 0;
        var notConverged = 0;

        var current = segment.GetSizes().ToList();
        Dictionary<double, int>? usedDiameters = null;

        // 1) Add missing / correct differing reference sizes.
        foreach (var reference in snapshot.Sizes)
        {
            var match = current.FirstOrDefault(
                s => Math.Abs(s.NominalDiameter - reference.NominalDiameter) < DiameterTolerance);
            if (match is null)
            {
                try
                {
                    segment.AddSize(ToMEPSize(reference));
                    added++;
                }
                catch (Exception ex)
                {
                    notConverged++;
                    SmartConLogger.Debug(
                        $"Segment '{snapshot.Name}': AddSize failed — {ex.Message}");
                }
                continue;
            }

            if (SizeMatches(match, reference)) continue;

            // Correction = remove + add. An in-use size cannot be removed,
            // and usage is verifiable only for pipe segments.
            if (segment is not PipeSegment)
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Segment '{snapshot.Name}': size DN {FormatMm(match.NominalDiameter)} differs " +
                    "from the reference; correction is supported only for pipe segments (usage is " +
                    "not verifiable for this segment kind). " +
                    "[Action: correct the size manually, then re-run the sync]");
                continue;
            }
            usedDiameters ??= CollectUsedDiameters(activeDoc, segment);
            if (IsSizeUsed(segment, match, usedDiameters) || current.Count - removed <= 1)
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Segment '{snapshot.Name}': size DN {FormatMm(match.NominalDiameter)} differs " +
                    "from the reference but is in use and cannot be corrected. " +
                    "[Action: re-route the affected MEP curves to another size, then re-run the sync]");
                continue;
            }
            try
            {
                segment.RemoveSize(match.NominalDiameter);
                segment.AddSize(ToMEPSize(reference));
                added++;
            }
            catch (Exception ex)
            {
                notConverged++;
                SmartConLogger.Debug(
                    $"Segment '{snapshot.Name}': size correction failed — {ex.Message}");
            }
        }

        // 2) Remove project-only sizes that are not in the reference.
        // Usage is verified only for pipes (RBS_PIPE_SEGMENT_PARAM); ducts
        // and other MEPCurve kinds do not expose their segment reference via
        // a parameter, so for non-pipe segments removals are skipped
        // entirely — safe default, reported as not converged.
        var isPipeSegment = segment is PipeSegment;
        foreach (var size in current)
        {
            var inReference = snapshot.Sizes.Any(
                r => Math.Abs(r.NominalDiameter - size.NominalDiameter) < DiameterTolerance);
            if (inReference) continue;

            if (!isPipeSegment)
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Segment '{snapshot.Name}': size DN {FormatMm(size.NominalDiameter)} is not in the " +
                    "reference; removal is supported only for pipe segments (usage is not verifiable " +
                    "for this segment kind). " +
                    "[Action: remove the size manually if it is unused, then re-run the sync]");
                continue;
            }

            usedDiameters ??= CollectUsedDiameters(activeDoc, segment);
            if (current.Count - removed <= 1)
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Segment '{snapshot.Name}': the last remaining size DN {FormatMm(size.NominalDiameter)} " +
                    "cannot be removed (Revit limitation). " +
                    "[Action: add at least one reference size first, then re-run the sync]");
                continue;
            }
            if (IsSizeUsed(segment, size, usedDiameters))
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Segment '{snapshot.Name}': size DN {FormatMm(size.NominalDiameter)} is not in the " +
                    "reference but is used by placed MEP curves and cannot be removed. " +
                    "[Action: re-route the affected MEP curves to a reference size, then re-run the sync]");
                continue;
            }
            try
            {
                segment.RemoveSize(size.NominalDiameter);
                removed++;
            }
            catch (Exception ex)
            {
                notConverged++;
                SmartConLogger.Debug(
                    $"Segment '{snapshot.Name}': RemoveSize failed — {ex.Message}");
            }
        }

        try { segment.Roughness = snapshot.Roughness; }
        catch { /* roughness is best-effort */ }

        if (added > 0 || removed > 0)
        {
            SmartConLogger.Info(
                $"Segment '{snapshot.Name}': sizes +{added}/-{removed}, not converged: {notConverged}.");
        }
        return new SegmentSyncResult(segment.Id, added, removed, notConverged);
    }

    /// <summary>
    /// Nominal diameters currently used by placed pipes on the given segment,
    /// collected once per segment sync (collector over MEPCurve is
    /// expensive — never run it per size). Only pipes expose their segment
    /// reference (<c>RBS_PIPE_SEGMENT_PARAM</c>), so this usage check is
    /// pipe-only by design.
    /// </summary>
    private static Dictionary<double, int> CollectUsedDiameters(Document doc, Segment segment)
    {
        var result = new Dictionary<double, int>();
        using var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(MEPCurve))
            .WhereElementIsNotElementType();
        foreach (var curve in collector.Cast<MEPCurve>())
        {
            var segmentParam = curve.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM);
            if (segmentParam is null) continue;
            ElementId? segmentId = null;
            try { segmentId = segmentParam.AsElementId(); } catch { continue; }
            if (segmentId != segment.Id) continue;

            var diameterParam = curve.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                ?? curve.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (diameterParam is null) continue;
            double diameter;
            try { diameter = diameterParam.AsDouble(); } catch { continue; }

            var key = Math.Round(diameter, 9);
            result[key] = result.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        return result;
    }

    private static bool IsSizeUsed(Segment segment, MEPSize size, Dictionary<double, int> usedDiameters)
    {
        return usedDiameters.ContainsKey(Math.Round(size.NominalDiameter, 9));
    }

    private static bool SizeMatches(MEPSize current, SegmentSizeSnapshot reference)
    {
        return Math.Abs(current.InnerDiameter - reference.InnerDiameter) < DiameterTolerance &&
               Math.Abs(current.OuterDiameter - reference.OuterDiameter) < DiameterTolerance &&
               current.UsedInSizeLists == reference.UsedInSizeLists &&
               current.UsedInSizing == reference.UsedInSizing;
    }

    private static MEPSize ToMEPSize(SegmentSizeSnapshot size)
    {
        return new MEPSize(
            size.NominalDiameter, size.InnerDiameter, size.OuterDiameter,
            size.UsedInSizeLists, size.UsedInSizing);
    }

    private static ElementId? EnsurePipeSchedule(Document doc, string scheduleName)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(PipeScheduleType));
        var existing = collector.Cast<PipeScheduleType>()
            .FirstOrDefault(s => string.Equals(s.Name, scheduleName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;

        try
        {
            return PipeScheduleType.Create(doc, scheduleName).Id;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"PipeScheduleType.Create '{scheduleName}': {ex.Message}");
            return null;
        }
    }

    private static Segment? FindSegmentByName(Document doc, string name)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Segment));
        return collector.Cast<Segment>()
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatMm(double feet)
    {
        return (feet * 304.8).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
