using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>mini-project-marker-v1</c>, Issue #189):
/// backfills the mini-project ES marker (#188) into every staged .rvt version
/// created before the marker existed. Optional because the marker is a UX
/// feature (safe close after "Импорт активного файла" #186, auto-switch
/// guard), not a data-integrity invariant — the path-fallback guard keeps
/// working meanwhile.
/// <para>
/// FIRST task that WRITES into managed files instead of the catalog
/// database: per variant it delegates to
/// <see cref="IMiniProjectActualizationService"/> (own Revit marshalling —
/// open → skip when already marked → clear read-only (I-16 exception) →
/// mark → <c>Document.Save()</c> in place → delete Revit backups → restore
/// read-only → close). The engine's snapshot context is NOT used — the
/// marker is a file-level artifact, not extraction content.
/// </para>
/// <para>
/// Marker semantics (V28 column <c>catalog_versions.es_marker_version</c>,
/// ADR-050 convention shared with the hash task): 0 = pending, 1 = marked
/// (or already marked — idempotent), -1 = unreadable, -2 = missing file.
/// Terminal markers are never retried. Trade-off (review M2): a per-variant
/// failure (locked file, network share) also lands in terminal -1 — the
/// engine summary counts the group as updated; the Warn + Action in the
/// log carries the failure. Accepted for consistency with the hash task.
/// </para>
/// </summary>
internal sealed class MiniProjectMarkerActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IMiniProjectActualizationService _markerService;

    public MiniProjectMarkerActualizationTask(
        LocalCatalogDatabase database,
        IMiniProjectActualizationService markerService)
        : base(database)
    {
        _markerService = markerService ?? throw new ArgumentNullException(nameof(markerService));
    }

    public override string Id => "mini-project-marker-v1";
    public override int Order => 60;
    public override bool IsCritical => false;

    /// <summary>File-level task: owns open/save via
    /// <see cref="IMiniProjectActualizationService"/> — the engine skips its
    /// extraction for marker-only groups (no double open, no terminal marker
    /// from an unrelated extraction failure).</summary>
    public override bool RequiresExtraction => false;

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'system'
          AND cv.es_marker_version = 0
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope(
            "MiniProjectMarker",
            ("Method", nameof(ApplyAsync)),
            ("Item", context.Group.ItemName),
            ("Variants", context.Group.Variants.Count));

        var dbRoot = Database.GetDatabaseRoot();
        foreach (var variant in context.Group.Variants)
        {
            ct.ThrowIfCancellationRequested();

            // Review M1: a variant NEWER than the openable one cannot be
            // opened by this Revit (OpenDocumentFile would throw — and the
            // terminal -1 would then skip the file FOREVER, a silent
            // permanent gap after the user's upgrade). Leave it pending (0):
            // the group re-runs cheaply on a newer host (AlreadyMarked short
            // circuit) and the variant gets marked there.
            if (variant.RevitMajorVersion > context.OpenedVariant.RevitMajorVersion)
            {
                SmartConLogger.Debug(
                    $"Variant Revit {variant.RevitMajorVersion} skipped — newer than the openable " +
                    $"{context.OpenedVariant.RevitMajorVersion}; stays pending until a newer host");
                continue;
            }

            var absolutePath = Path.Combine(dbRoot ?? string.Empty, variant.RelativePath);

            var outcome = await _markerService
                .MarkManagedFileAsync(absolutePath, context.Group.CatalogItemId, ct)
                .ConfigureAwait(false);

            var marker = outcome.Status switch
            {
                MiniProjectMarkFileStatus.Marked => 1,
                MiniProjectMarkFileStatus.AlreadyMarked => 1,
                MiniProjectMarkFileStatus.Missing => -2,
                _ => -1,
            };
            await WriteVariantMarkerAsync(variant.VersionId, marker, ct).ConfigureAwait(false);
        }

        SmartConLogger.Info(
            $"mini-project-marker-v1: group '{context.Group.ItemName}' processed " +
            $"({context.Group.Variants.Count} variant(s))");
    }

    public override async Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        // Terminal markers (ADR-050 §2, same convention as the hash task):
        // -2 missing file, -1 unreadable — never retried.
        var marker = kind == ActualizationFailureKind.MissingFile ? -2 : -1;
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE catalog_versions
            SET es_marker_version = @marker
            WHERE id IN ({VariantIdParams(cmd, group.Variants)})
            """;
        cmd.Parameters.Add(new SqliteParameter("@marker", marker));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteVariantMarkerAsync(string versionId, int marker, CancellationToken ct)
    {
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE catalog_versions SET es_marker_version = @marker WHERE id = @vid";
        cmd.Parameters.Add(new SqliteParameter("@marker", marker));
        cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string VariantIdParams(SqliteCommand cmd, IReadOnlyList<ActualizationVariant> variants)
    {
        var idParams = new string[variants.Count];
        for (var p = 0; p < variants.Count; p++)
        {
            idParams[p] = "@vid" + p;
            cmd.Parameters.Add(new SqliteParameter(idParams[p], variants[p].VersionId));
        }
        return string.Join(", ", idParams);
    }
}
