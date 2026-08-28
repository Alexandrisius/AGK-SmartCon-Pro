using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>routing-backfill-v1</c>, Issue #254,
/// ADR-072 Phase 2b): heals pre-refactor catalog versions —
/// <list type="number">
///   <item>BACKFILL: writes <c>family_routing_rules</c> /
///   <c>family_routing_type_settings</c> (V34) for legacy versions. The
///   primary source is file-free: the ROUTING/FAMKEY canonical sections in
///   <c>catalog_versions.section_strings</c> (V33,
///   <see cref="RoutingSectionParser"/>); the fallback for versions
///   without section strings is the pre-slim extraction of the opened
///   mini-project (its legacy full routing).</item>
///   <item>SLIMMING: the staged mini is healed to the new reference shape
///   via <see cref="IMiniProjectRoutingSlimmingService"/> (fitting rules
///   removed, fitting instances/families deleted, orphan materials —
///   the #254 duplicates included — deleted, suffixed working copies
///   renamed, saved in place, backups deleted, read-only restored).</item>
/// </list>
/// Detection keys off the tracking column
/// <c>catalog_versions.routing_backfilled</c> (V35): 0 = pending,
/// 1 = done, -1 = unreadable, -2 = missing — NEVER off the absence of
/// routing rows (a legitimately routing-less type has none).
/// Optional because sync has a non-destructive legacy fallback (reads
/// routing from the mini) until the version is healed.
/// </summary>
internal sealed class RoutingBackfillActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IMiniProjectRoutingSlimmingService _slimmingService;
    private readonly IFamilyRoutingRuleRepository _routingRuleRepository;

    public RoutingBackfillActualizationTask(
        LocalCatalogDatabase database,
        IMiniProjectRoutingSlimmingService slimmingService,
        IFamilyRoutingRuleRepository routingRuleRepository)
        : base(database)
    {
        _slimmingService = slimmingService ?? throw new ArgumentNullException(nameof(slimmingService));
        _routingRuleRepository = routingRuleRepository ?? throw new ArgumentNullException(nameof(routingRuleRepository));
    }

    public override string Id => "routing-backfill-v1";
    public override int Order => 65;
    public override bool IsCritical => false;

    /// <summary>File-level task: owns open/save via
    /// <see cref="IMiniProjectRoutingSlimmingService"/> — the engine skips
    /// its extraction (same pattern as the marker task).</summary>
    public override bool RequiresExtraction => false;

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'system'
          AND cv.routing_backfilled = 0
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope(
            "RoutingBackfill",
            ("Method", nameof(ApplyAsync)),
            ("Item", context.Group.ItemName),
            ("Variants", context.Group.Variants.Count));

        var dbRoot = Database.GetDatabaseRoot();
        foreach (var variant in context.Group.Variants)
        {
            ct.ThrowIfCancellationRequested();

            // Same newer-host rule as the marker task: a variant newer than
            // this Revit cannot be opened — leave it pending (0), never
            // terminal.
            if (variant.RevitMajorVersion > context.OpenedVariant.RevitMajorVersion)
            {
                SmartConLogger.Debug(
                    $"Variant Revit {variant.RevitMajorVersion} skipped — newer than the openable " +
                    $"{context.OpenedVariant.RevitMajorVersion}; stays pending until a newer host");
                continue;
            }

            var absolutePath = Path.Combine(dbRoot ?? string.Empty, variant.RelativePath);
            if (!File.Exists(absolutePath))
            {
                await WriteVariantMarkerAsync(variant.VersionId, -2, ct).ConfigureAwait(false);
                continue;
            }

            // Pass A (file-free): section strings carry the full pre-FHV19
            // routing — backfill without opening the file.
            var backfilled = await TryBackfillFromSectionStringsAsync(variant, context.Group.CatalogItemId, ct)
                .ConfigureAwait(false);

            // Pass B (Revit): slimming always needs the file; when the
            // file-free pass found no section strings, the pre-slim
            // extraction of THIS open is the backfill source.
            var outcome = await _slimmingService
                .SlimManagedFileAsync(absolutePath, ct)
                .ConfigureAwait(false);

            if (!backfilled && outcome.PreSlimSnapshot is not null)
            {
                await BackfillFromSnapshotAsync(outcome.PreSlimSnapshot, variant, context, ct)
                    .ConfigureAwait(false);
                backfilled = true;
            }

            if (!backfilled)
            {
                SmartConLogger.Debug(
                    $"Variant '{variant.VersionId}': no routing source found (routing-less category or already stored) — rules untouched");
            }

            var marker = outcome.Status switch
            {
                MiniProjectSlimmingStatus.Slimmed => 1,
                MiniProjectSlimmingStatus.AlreadySlim => 1,
                MiniProjectSlimmingStatus.Missing => -2,
                _ => -1,
            };
            await WriteVariantMarkerAsync(variant.VersionId, marker, ct).ConfigureAwait(false);
        }

        SmartConLogger.Info(
            $"routing-backfill-v1: group '{context.Group.ItemName}' processed ({context.Group.Variants.Count} variant(s))");
    }

    public override async Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        var marker = kind == ActualizationFailureKind.MissingFile ? -2 : -1;
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE catalog_versions
            SET routing_backfilled = @marker
            WHERE id IN ({VariantIdParams(cmd, group.Variants)})
            """;
        cmd.Parameters.Add(new SqliteParameter("@marker", marker));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pass A: parse ROUTING/FAMKEY sections from
    /// <c>catalog_versions.section_strings</c> and write the version's
    /// routing records. <c>false</c> when the version carries no section
    /// strings (pass B falls back to the opened mini).
    /// </summary>
    private async Task<bool> TryBackfillFromSectionStringsAsync(
        ActualizationVariant variant, string catalogItemId, CancellationToken ct)
    {
        string? sectionStringsJson = null;
        using (var connection = Database.CreateConnection())
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT section_strings FROM catalog_versions WHERE id = @vid";
            cmd.Parameters.Add(new SqliteParameter("@vid", variant.VersionId));
            sectionStringsJson = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string;
        }

        var sections = ContentSectionJsonSerializer.Deserialize(sectionStringsJson);
        if (sections is null)
            return false;

        var parsed = RoutingSectionParser.Parse(sections);
        if (parsed.Count == 0)
            return false;

        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        foreach (var type in parsed)
        {
            RoutingRuleRecordMapper.ToRecords(
                new SystemTypeSnapshot(type.TypeName, [], Routing: type.Routing, FamilyKey: type.FamilyKey),
                rules, settings);
        }

        await _routingRuleRepository
            .ReplaceForVersionAsync(catalogItemId, variant.VersionId, rules, settings, ct)
            .ConfigureAwait(false);
        SmartConLogger.Info(
            $"routing-backfill-v1: variant '{variant.VersionId}' backfilled file-free from section strings " +
            $"({rules.Count} rules, {settings.Count} settings)");
        return true;
    }

    /// <summary>
    /// Pass B: build the records from the pre-slim snapshot of the opened
    /// mini, trimmed to the catalog's authoritative type names (same
    /// helper as the hash task — the mini also carries renamed template
    /// leftovers whose routing must not be backfilled).
    /// </summary>
    private async Task BackfillFromSnapshotAsync(
        SystemFamilySnapshot preSlimSnapshot,
        ActualizationVariant variant,
        FamilyActualizationContext context,
        CancellationToken ct)
    {
        var trimmed = await SystemTypeCatalogTrimHelper
            .TrimToCatalogTypeNamesAsync(preSlimSnapshot, context.Group, Database, ct)
            .ConfigureAwait(false);

        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        foreach (var type in trimmed.Types)
        {
            RoutingRuleRecordMapper.ToRecords(type, rules, settings);
        }

        await _routingRuleRepository
            .ReplaceForVersionAsync(context.Group.CatalogItemId, variant.VersionId, rules, settings, ct)
            .ConfigureAwait(false);
        SmartConLogger.Info(
            $"routing-backfill-v1: variant '{variant.VersionId}' backfilled from the pre-slim mini extraction " +
            $"({rules.Count} rules, {settings.Count} settings)");
    }

    private async Task WriteVariantMarkerAsync(string versionId, int marker, CancellationToken ct)
    {
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE catalog_versions SET routing_backfilled = @marker WHERE id = @vid";
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
