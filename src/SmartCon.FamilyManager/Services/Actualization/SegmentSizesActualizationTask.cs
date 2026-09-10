using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// ADR-072 Phase 3 (V36): backfills <c>family_segment_sizes</c> for legacy
/// pipe versions imported before the editor existed — the routing editor's
/// min/max dropdowns need the segment nominal diameters, and pre-Phase-3
/// versions never stored them. Detection: system pipe versions (the only
/// category with Segment elements, ADR-072 §1.7) that have catalog types
/// but no size rows. Extraction-required: the sizes come from the staged
/// mini's segment tables via the engine's system snapshot.
/// </summary>
internal sealed class SegmentSizesActualizationTask : SqlDetectionActualizationTaskBase
{
    public SegmentSizesActualizationTask(LocalCatalogDatabase database)
        : base(database)
    {
    }

    public override string Id => "segment-sizes-v1";
    public override int Order => 75;
    public override bool IsCritical => false;

    protected override string DetectionSql => $"""
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'system'
          AND ci.revit_category_id = {RoutingGroupCatalog.PipeCurvesCategoryId}
          AND COALESCE(cv.hash_format_version, 0) NOT IN (-1, -2)
          AND EXISTS(SELECT 1 FROM family_types ft WHERE ft.version_id = cv.id)
          AND NOT EXISTS(SELECT 1 FROM family_segment_sizes fss WHERE fss.catalog_version_id = cv.id)
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        if (context.SystemSnapshot is null)
        {
            SmartConLogger.Warn(
                $"segment-sizes-v1: no system snapshot for '{context.OpenedVariant.FileName}' " +
                "[Action: группа останется pending; переимпортируйте системную категорию]");
            return;
        }

        var sizes = BuildRecords(context.SystemSnapshot);
        if (sizes.Count == 0)
        {
            // Pathological edge (broken extraction): persist a nominal=0
            // sentinel so the NOT EXISTS detection predicate clears and the
            // version is not re-opened on every «Обновить базу». nominal=0
            // never reaches the editor dropdowns (ReadDistinctNominals
            // filters it) and never copies as a real size.
            SmartConLogger.Warn(
                $"segment-sizes-v1: the system snapshot of '{context.OpenedVariant.FileName}' carries no segment sizes — writing the pending sentinel. " +
                "[Action: данные рассинхронизированы — переимпортируйте системную категорию для восстановления размеров]");
            sizes.Add(new SegmentSizeRecord("(none)", 0, 0, 0, false, false, 0));
        }

        // One write for ALL Revit variants of the group (identical content).
        foreach (var variant in context.Group.Variants)
        {
            ct.ThrowIfCancellationRequested();
            using var connection = Database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var tx = connection.BeginTransaction();
            try
            {
                using (var del = connection.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM family_segment_sizes WHERE catalog_version_id = @versionId";
                    del.Parameters.Add(new SqliteParameter("@versionId", variant.VersionId));
                    await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                foreach (var size in sizes)
                {
                    using var ins = connection.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT OR REPLACE INTO family_segment_sizes
                            (catalog_version_id, segment_name, nominal_diameter, inner_diameter,
                             outer_diameter, used_in_size_lists, used_in_sizing, sort_order)
                        VALUES (@version, @segment, @nominal, @inner, @outer, @lists, @sizing, @order)
                        """;
                    ins.Parameters.Add(new SqliteParameter("@version", variant.VersionId));
                    ins.Parameters.Add(new SqliteParameter("@segment", size.SegmentName));
                    ins.Parameters.Add(new SqliteParameter("@nominal", size.NominalDiameter));
                    ins.Parameters.Add(new SqliteParameter("@inner", size.InnerDiameter));
                    ins.Parameters.Add(new SqliteParameter("@outer", size.OuterDiameter));
                    ins.Parameters.Add(new SqliteParameter("@lists", size.UsedInSizeLists ? 1 : 0));
                    ins.Parameters.Add(new SqliteParameter("@sizing", size.UsedInSizing ? 1 : 0));
                    ins.Parameters.Add(new SqliteParameter("@order", size.SortOrder));
                    await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        SmartConLogger.Info(
            $"segment-sizes-v1: wrote {sizes.Count} size row(s) for '{context.Group.ItemName}' " +
            $"({context.Group.VersionLabel}, {context.Group.Variants.Count} variant(s))");
    }

    private static List<SegmentSizeRecord> BuildRecords(SystemFamilySnapshot snapshot)
    {
        var sizes = new List<SegmentSizeRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in snapshot.Types)
        {
            if (type.Segments is null)
                continue;
            var order = 0;
            foreach (var segment in type.Segments)
            {
                foreach (var size in segment.Sizes)
                {
                    var key = segment.Name + "|" +
                        size.NominalDiameter.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                    if (!seen.Add(key))
                        continue;
                    sizes.Add(new SegmentSizeRecord(
                        segment.Name,
                        size.NominalDiameter,
                        size.InnerDiameter,
                        size.OuterDiameter,
                        size.UsedInSizeLists,
                        size.UsedInSizing,
                        order++));
                }
            }
        }
        return sizes;
    }
}
