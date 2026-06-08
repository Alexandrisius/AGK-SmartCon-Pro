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

            if (match is null || !match.Success || match.WasSkippedAsDuplicate || string.IsNullOrEmpty(match.CatalogItemId))
            {
                skipped++;
                continue;
            }

            imported++;

            try
            {
                // I-01: ResolveTypesFromRfa calls Revit API (app.OpenDocumentFile,
                // familyDoc.FamilyManager) and must execute on the Revit UI
                // thread. The WPF DockablePane SynchronizationContext is NOT
                // the Revit UI thread, so the await of ImportBatchAsync
                // (which uses ConfigureAwait(false) internally) cannot be
                // relied upon to resume on Revit. Route the Revit-API call
                // through IFamilyManagerAwaitableEvent, which is bound to
                // an ExternalEvent handler.
                var types = await _awaitableEvent.RaiseAsync(_ =>
                    _typeResolver.ResolveTypesFromRfa(
                        item.FilePath, match.CatalogItemId!, match.VersionId, match.FileId),
                    ct);
                if (types.Count == 0)
                {
                    SmartConLogger.Warn(
                        $"[LoadableImport] No types for '{item.FileName}'");
                }
                else
                {
                    await _typeRepository.SaveTypesAsync(match.CatalogItemId!, types, ct);
                    SmartConLogger.Info(
                        $"[LoadableImport] Saved {types.Count} type(s) for '{item.FileName}' (CatalogItemId={match.CatalogItemId})");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"[LoadableImport] Failed to persist types for '{item.FileName}': {ex.Message}");
            }

            try
            {
                var resolved = await _fileResolver.ResolveForLoadAsync(
                    match.CatalogItemId!, targetRevitVersion, ct);
                if (!string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    var txtPath = Path.ChangeExtension(resolved.AbsolutePath, ".txt");
                    attributeTasks.Add(new LoadableFamilyAttributeTask(
                        CatalogItemId: match.CatalogItemId!,
                        ManagedRfaPath: resolved.AbsolutePath,
                        VersionId: match.VersionId,
                        FileId: match.FileId,
                        HasTypeCatalog: File.Exists(txtPath)));
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"[LoadableImport] ResolveForLoadAsync failed for '{item.FileName}': {ex.Message}");
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
