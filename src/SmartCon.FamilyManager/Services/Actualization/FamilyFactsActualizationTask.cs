using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>family-facts-v1</c>): backfills
/// <c>catalog_items.revit_category_id</c> and the <c>family_facts</c> rows
/// for items imported before the facts subsystem existed (V22 added the
/// column and the table empty; forward-fill at import covers only new
/// imports). Optional because facts are cosmetic (properties-window
/// display) — missing them corrupts nothing, so no read-only gate, only
/// the amber indicator + "Обновить базу" command.
/// Scope: loadable AND system, ACTIVE label — facts and the category id
/// are item-level artifacts written once from the active version's file
/// (system groups get only the category id from the registry-driven
/// <c>ExtractSystemCategoryAsync</c> — no rules target system categories).
/// The detection SQL is GENERATED from <see cref="FamilyFactRuleSet"/>:
/// registering a new (category, fact) rule automatically extends the
/// detection — no task or SQL edits needed (ADR-055).
/// Failure policy: no terminal marker — a missing/unreadable file stays
/// pending and self-heals on the next run when the file becomes available
/// (same tradeoff as <see cref="RevitCategoryActualizationTask"/>). ADR-054.
/// </summary>
internal sealed class FamilyFactsActualizationTask : SqlDetectionActualizationTaskBase
{
    /// <summary>
    /// Sentinel written when extraction determined the item has no readable
    /// category (family doc without OwnerFamily category / empty staged
    /// project). Clears the IS NULL detection without inventing a value;
    /// no rule is registered for -1, so no facts are required either.
    /// Mirrors the empty-string sentinel of
    /// <see cref="RevitCategoryActualizationTask"/> for the string column.
    /// </summary>
    private const int UnknownCategoryId = -1;

    public FamilyFactsActualizationTask(LocalCatalogDatabase database)
        : base(database)
    {
    }

    public override string Id => "family-facts-v1";
    public override int Order => 50;
    public override bool IsCritical => false;

    protected override string DetectionSql { get; } = BuildDetectionSql();

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        var categoryId = context.Snapshot.CategoryId ?? UnknownCategoryId;

        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            // The sentinel never overwrites a real category id (a group can
            // reach Apply via the missing-fact branch with the id already
            // set while this run's extraction failed to read the category).
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE catalog_items
                    SET revit_category_id = @categoryId
                    WHERE id = @itemId
                      AND (revit_category_id IS NULL OR @categoryId <> @unknownId)
                    """;
                cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));
                cmd.Parameters.Add(new SqliteParameter("@itemId", context.Group.CatalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@unknownId", UnknownCategoryId));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Facts come from the same single-open snapshot. Null (system
            // groups, categories without rules) = nothing to write — the
            // detection for such categories requires no facts. Present =
            // full replace, including empty-string sentinels for
            // evaluated-but-absent parameters (they clear the detection).
            if (context.Snapshot.Facts is not null)
            {
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = "DELETE FROM family_facts WHERE catalog_item_id = @itemId";
                    deleteCmd.Parameters.Add(new SqliteParameter("@itemId", context.Group.CatalogItemId));
                    await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (var fact in context.Snapshot.Facts)
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = """
                        INSERT INTO family_facts (catalog_item_id, fact_key, value_key, value_display)
                        VALUES (@itemId, @factKey, @valueKey, @valueDisplay)
                        """;
                    insertCmd.Parameters.Add(new SqliteParameter("@itemId", context.Group.CatalogItemId));
                    insertCmd.Parameters.Add(new SqliteParameter("@factKey", fact.FactKey));
                    insertCmd.Parameters.Add(new SqliteParameter("@valueKey", fact.ValueKey));
                    insertCmd.Parameters.Add(new SqliteParameter("@valueDisplay", fact.ValueDisplay));
                    await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Detection fragment generated from <see cref="FamilyFactRuleSet"/>:
    /// an item is pending when its category id was never written (pre-V22
    /// row or an import path without a snapshot), or when its category has
    /// registered rules and any required fact row is missing. One
    /// parenthesised clause per category; per-category clauses OR the
    /// per-key NOT EXISTS checks so a multi-fact category is pending until
    /// ALL its facts exist.
    /// </summary>
    private static string BuildDetectionSql()
    {
        var clauses = new List<string>();
        foreach (var group in FamilyFactRuleSet.Rules.GroupBy(r => r.CategoryId))
        {
            var keyClauses = string.Join(" OR ", group.Select(r =>
                "NOT EXISTS(SELECT 1 FROM family_facts ff WHERE ff.catalog_item_id = ci.id AND ff.fact_key = '"
                + r.FactKey + "')"));
            clauses.Add($"(ci.revit_category_id = {group.Key} AND ({keyClauses}))");
        }

        var categoryBlock = clauses.Count > 0
            ? "\n           OR " + string.Join("\n           OR ", clauses)
            : string.Empty;

        return $"""
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            WHERE ci.family_source IN ('loadable', 'system')
              AND cv.version_label = ci.current_version_label
              AND (ci.revit_category_id IS NULL{categoryBlock})
            """;
    }
}
