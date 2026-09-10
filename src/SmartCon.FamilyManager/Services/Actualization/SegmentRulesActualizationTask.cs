using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>segment-rules-v1</c>, FHV21): backfills
/// <c>family_segment_rules</c> (V38) for pipe versions imported before the
/// per-version segment store existed — their segment configuration lives
/// only in the staged mini (and the legacy item/V34 rows the readers fall
/// back to). Detection: system pipe versions that have catalog types but no
/// segment-rule rows. Extraction-required: the rules come from the staged
/// mini's Segments routing group via the engine's system snapshot. Optional
/// because readers fall back to the stored Segments rows until the backfill
/// lands — missing rows corrupt nothing (amber indicator only, ADR-054).
/// </summary>
internal sealed class SegmentRulesActualizationTask : SqlDetectionActualizationTaskBase
{
    public SegmentRulesActualizationTask(LocalCatalogDatabase database)
        : base(database)
    {
    }

    public override string Id => "segment-rules-v1";
    public override int Order => 76;
    public override bool IsCritical => false;

    protected override string DetectionSql => $"""
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'system'
          AND ci.revit_category_id = {RoutingGroupCatalog.PipeCurvesCategoryId}
          AND COALESCE(cv.hash_format_version, 0) NOT IN (-1, -2)
          AND EXISTS(SELECT 1 FROM family_types ft WHERE ft.version_id = cv.id)
          AND NOT EXISTS(SELECT 1 FROM family_segment_rules fsr WHERE fsr.catalog_version_id = cv.id)
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        if (context.SystemSnapshot is null)
        {
            SmartConLogger.Warn(
                $"segment-rules-v1: no system snapshot for '{context.OpenedVariant.FileName}' " +
                "[Action: группа останется pending; переимпортируйте системную категорию]");
            return;
        }

        var records = SegmentRuleComposition.FromSnapshot(context.SystemSnapshot);
        if (records.Count == 0)
        {
            SmartConLogger.Warn(
                $"segment-rules-v1: the system snapshot of '{context.OpenedVariant.FileName}' carries no segment rules. " +
                "[Action: группа останется pending — если это труба, проверьте мини-проект; для прочих категорий задача не предназначена]");
            return;
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
                    del.CommandText = "DELETE FROM family_segment_rules WHERE catalog_version_id = @versionId";
                    del.Parameters.Add(new SqliteParameter("@versionId", variant.VersionId));
                    await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                foreach (var record in records)
                {
                    using var ins = connection.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT OR REPLACE INTO family_segment_rules
                            (catalog_version_id, family_key, type_name, rule_order, segment_name,
                             min_size_feet, max_size_feet, description)
                        VALUES (@version, @familyKey, @typeName, @order, @segment, @min, @max, @description)
                        """;
                    ins.Parameters.Add(new SqliteParameter("@version", variant.VersionId));
                    ins.Parameters.Add(new SqliteParameter("@familyKey", record.FamilyKey));
                    ins.Parameters.Add(new SqliteParameter("@typeName", record.TypeName));
                    ins.Parameters.Add(new SqliteParameter("@order", record.RuleOrder));
                    ins.Parameters.Add(new SqliteParameter("@segment", record.SegmentName));
                    ins.Parameters.Add(new SqliteParameter("@min",
                        record.MinSizeFeet is { } min ? min : (object)DBNull.Value));
                    ins.Parameters.Add(new SqliteParameter("@max",
                        record.MaxSizeFeet is { } max ? max : (object)DBNull.Value));
                    ins.Parameters.Add(new SqliteParameter("@description", record.Description));
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
            $"segment-rules-v1: wrote {records.Count} rule row(s) for '{context.Group.ItemName}' " +
            $"({context.Group.VersionLabel}, {context.Group.Variants.Count} variant(s))");
    }
}
