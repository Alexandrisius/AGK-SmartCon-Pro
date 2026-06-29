using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

internal sealed class LoadableFamilyImportOrchestrator : ILoadableFamilyImportOrchestrator
{
    private readonly IFamilyImportService _importService;
    private readonly ILoadableFamilyTypeResolver _typeResolver;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;

    public LoadableFamilyImportOrchestrator(
        IFamilyImportService importService,
        ILoadableFamilyTypeResolver typeResolver,
        IFamilyTypeRepository typeRepository,
        IFamilyFileResolver fileResolver,
        IFamilyManagerAwaitableEvent awaitableEvent)
    {
        _importService = importService;
        _typeResolver = typeResolver;
        _typeRepository = typeRepository;
        _fileResolver = fileResolver;
        _awaitableEvent = awaitableEvent;
    }

    public async Task<LoadableFamilyImportResult> ImportAndPersistTypesAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        int targetRevitVersion,
        string? categoryId = null,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return new LoadableFamilyImportResult(true, "No items", 0, 0, []);
        }

        var importResult = await _importService.ImportBatchAsync(
            items, categoryId, progress: null, ct);

        var attributeTasks = new List<LoadableFamilyAttributeTask>();
        var imported = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            // item.FileName is set via SafeFileName.GetBaseName(item.FilePath) in
            // every Build*BatchItem path (NO extension). However, r.FileName comes
            // from LocalFamilyImportService.ImportFileAsync:79, which assigns
            // metadata.FileName — the raw file name with extension (".rfa").
            // Compare on the WITH-extension form on both sides so the match works
            // for every family (including those with internal dots like
            // "BP_A0307_ITAP_ART.162_Амер угловая.rfa").
            var itemFileWithExt = Path.GetFileName(item.FilePath);
            var match = importResult.Results.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r.FileName) &&
                string.Equals(r.FileName, itemFileWithExt, StringComparison.OrdinalIgnoreCase));

            if (match is null || !match.Success || match.WasSkipped || string.IsNullOrEmpty(match.CatalogItemId))
            {
                skipped++;
                continue;
            }

            imported++;

            try
            {
                // Phase 27: use the in-memory snapshot from Prepare to build
                // FamilyTypeDescriptor rows WITHOUT re-opening the managed .rfa
                // via ResolveTypesFromRfa. The snapshot already contains every
                // type name + UniqueId (collected from FamilySymbol in the
                // single Prepare open). This eliminates the 42×
                // LoadableResolver.OpenDocumentFile calls seen in the post-import
                // flow. Pure C# — no ExternalEvent needed (no Revit API).
                var types = item.LoadableSnapshot is not null
                    ? SnapshotExtractionMapper.ToTypeDescriptors(
                        item.LoadableSnapshot,
                        match.CatalogItemId!,
                        match.VersionId,
                        match.FileId)
                    : [];

                if (types.Count == 0)
                {
                    using var _scope = SmartConLogger.BeginScope("LoadableImport", ("FileName", item.FileName));
                    var reason = item.LoadableSnapshot is null
                        ? "LoadableSnapshot is null (Prepare did not produce one)"
                        : "snapshot has 0 types";
                    SmartConLogger.Warn(
                        $"No types for '{item.FileName}': {reason} " +
                        "[Action: check Prepare logs — snapshot extraction may have failed, or family has no FamilyManager.Types]");
                }
                else
                {
                    // v2.0.0 (ADR-036): orchestrators pass null/null/"no-run" because
                    // they replace the entire type set for a catalog item (no multi-version
                    // semantics). SyncTypesAsync enforces DELETE+INSERT atomically.
                    await _typeRepository.SyncTypesAsync(match.CatalogItemId!, versionId: null, fileId: null, runId: "no-run", types, ct);
                    using var _scope = SmartConLogger.BeginScope("LoadableImport", ("FileName", item.FileName), ("CatalogItemId", match.CatalogItemId));
                    SmartConLogger.Info($"Saved {types.Count} type(s) for '{item.FileName}' (CatalogItemId={match.CatalogItemId}) [from snapshot, no re-open]");
                }
            }
            catch (Exception ex)
            {
                using var _scope = SmartConLogger.BeginScope("LoadableImport", ("FileName", item.FileName));
                SmartConLogger.Warn($"Failed to persist types: {ex.Message} [Action: Check database write permissions and SQLite file integrity]");
            }

            try
            {
                var resolved = await _fileResolver.ResolveForLoadAsync(
                    match.CatalogItemId!, targetRevitVersion, ct);
                if (!string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    attributeTasks.Add(new LoadableFamilyAttributeTask(
                        CatalogItemId: match.CatalogItemId!,
                        ManagedRfaPath: resolved.AbsolutePath,
                        VersionId: match.VersionId,
                        FileId: match.FileId,
                        Snapshot: item.LoadableSnapshot));
                }
            }
            catch (Exception ex)
            {
                using var _scope = SmartConLogger.BeginScope("LoadableImport", ("FileName", item.FileName));
                SmartConLogger.Warn($"ResolveForLoadAsync failed: {ex.Message} [Action: Verify file exists and Revit version matches catalog targetRevitVersion]");
            }
        }

        return new LoadableFamilyImportResult(
            Success: imported > 0,
            Message: imported > 0
                ? $"Imported {imported} loadable families"
                : "No loadable families imported",
            ImportedCount: imported,
            SkippedCount: skipped,
            AttributeTasks: attributeTasks);
    }
}
