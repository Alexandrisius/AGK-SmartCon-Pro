using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>revit-category-v1</c>): backfills
/// <c>catalog_items.revit_category</c> for items imported before the column
/// was populated at INSERT (V11 added the column without a backfill; the
/// live UC-3/UC-4 mapping passed NULL for both sources until the forward
/// fix). Optional because the value is cosmetic (properties-window
/// display) — missing it corrupts nothing, so no read-only gate, only the
/// amber indicator + "Обновить базу" command.
/// Scope: loadable AND system, ACTIVE label — the category is an
/// item-level column written once from the active version's file (system
/// groups are extracted by the engine via category-only
/// <c>ExtractSystemCategoryAsync</c>). Failure policy: no terminal
/// marker — a missing/unreadable file stays pending and self-heals on the
/// next run when the file becomes available (same tradeoff as
/// <see cref="AttributesActualizationTask"/>). ADR-054.
/// </summary>
internal sealed class RevitCategoryActualizationTask : SqlDetectionActualizationTaskBase
{
    public RevitCategoryActualizationTask(LocalCatalogDatabase database)
        : base(database)
    {
    }

    public override string Id => "revit-category-v1";
    public override int Order => 40;
    public override bool IsCritical => false;

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source IN ('loadable', 'system')
          AND cv.version_label = ci.current_version_label
          AND ci.revit_category IS NULL
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        // Empty-string marker: a snapshot without a category must still clear
        // the detection, otherwise the item stays pending forever. The UI
        // treats '' the same as NULL (shows "—").
        var category = string.IsNullOrWhiteSpace(context.Snapshot.Category)
            ? string.Empty
            : context.Snapshot.Category;

        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items
            SET revit_category = @category
            WHERE id = @itemId AND revit_category IS NULL
            """;
        cmd.Parameters.Add(new SqliteParameter("@category", category));
        cmd.Parameters.Add(new SqliteParameter("@itemId", context.Group.CatalogItemId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
