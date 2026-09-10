using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

public sealed class ProjectFamilyStagingService : IProjectFamilyStagingService
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IFamilyImportPreparationService _preparationService;
    private readonly IRevitContext _revitContext;
    private readonly ISystemFamilyIsolationProjectService _systemFamilyIsolationProject;
    private readonly IFamilyImportService _importService;
    private readonly IDatabaseManager _databaseManager;

    public ProjectFamilyStagingService(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IFamilyImportPreparationService preparationService,
        IRevitContext revitContext,
        ISystemFamilyIsolationProjectService systemFamilyIsolationProject,
        IFamilyImportService importService,
        IDatabaseManager databaseManager)
    {
        _awaitableEvent = awaitableEvent;
        _preparationService = preparationService;
        _revitContext = revitContext;
        _systemFamilyIsolationProject = systemFamilyIsolationProject;
        _importService = importService;
        _databaseManager = databaseManager;
    }

    public Task CloseAllPreparedDocumentsAsync(CancellationToken ct)
        => _preparationService.CloseAllPreparedDocumentsAsync(ct);

    public Task<FamilyBatchImportItem?> StageSystemAsync(FamilyBatchImportItem item, CancellationToken ct)
    {
        if (item.Source is not FamilyImportSource.SystemSource
            || !item.FilePath.StartsWith("system://", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<FamilyBatchImportItem?>(item);
        }

        // Issue #126 / ADR-041: MakeActive imports NO file — only the
        // active-version pointer is switched. Staging would write an
        // orphan managed .rvt no catalog row references.
        if (item.Action == FamilyBatchImportAction.MakeActive)
        {
            return Task.FromResult<FamilyBatchImportItem?>(item);
        }

        return _awaitableEvent.RaiseAsync(_ =>
        {
            var source = (FamilyImportSource.SystemSource)item.Source!;
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is null)
            {
                SmartConLogger.Warn(
                    "Active document is null — cannot stage system family " +
                    "[Action: откройте .rvt проект в Revit, затем повторите команду]");
                return (FamilyBatchImportItem?)null;
            }

            var managedRvtPath = ResolveManagedPath(item, source.DisplayName, ".rvt", out var generatedCatalogItemId);
            if (string.IsNullOrEmpty(managedRvtPath))
            {
                SmartConLogger.Warn(
                    $"Cannot compute managed path for '{source.DisplayName}' — skipping " +
                    "[Action: check active catalog DB is selected]");
                return (FamilyBatchImportItem?)null;
            }

            CreateCleanProjectResult createResult;
            try
            {
                var categoryEnum = (BuiltInCategory)source.CategoryId;
                // #188: pass the catalog item id so the staged mini-project
                // marker links the file to its catalog row (M3: including the
                // fallback-allocated id — otherwise ES id↔file link breaks).
                var catalogItemId = item.ExistingCatalogItemId
                    ?? item.PrecomputedCatalogItemId
                    ?? generatedCatalogItemId;
                createResult = _systemFamilyIsolationProject.CreateCleanProjectWithTypesAndInstances(
                    activeDoc, source.TypeUniqueIds, categoryEnum, source.DisplayName, managedRvtPath!, catalogItemId);
            }
            catch (Exception ex)
            {
                SmartConLogger.Error(
                    $"CreateCleanProjectWithTypesAndInstances threw for '{source.DisplayName}': {ex.GetType().Name}: {ex.Message} " +
                    "[Action: verify category has at least one placeable type in the active project]");
                return (FamilyBatchImportItem?)null;
            }

            if (!createResult.Success || string.IsNullOrEmpty(createResult.FilePath))
            {
                SmartConLogger.Warn(
                    $"CreateCleanProjectWithTypesAndInstances returned Success=false for '{source.DisplayName}' " +
                    "[Action: see prior log lines from SystemRevitOps for the underlying cause]");
                return (FamilyBatchImportItem?)null;
            }

            if (item.Action == FamilyBatchImportAction.OverwriteCurrent
                && !string.IsNullOrEmpty(item.ExistingCatalogItemId)
                && File.Exists(managedRvtPath!))
            {
                File.SetAttributes(managedRvtPath!, File.GetAttributes(managedRvtPath!) | FileAttributes.ReadOnly);
            }

            SmartConLogger.Info(
                $"Staged system family '{source.DisplayName}' -> '{createResult.FilePath}'");
            return item with
            {
                FilePath = createResult.FilePath!,
                SourceTypes = source.TypeNames
                    .Zip(source.TypeUniqueIds, (name, uid) => (name, uid))
                    .Select((pair, i) => new FamilySourceTypeInfo(
                        pair.uid,
                        pair.name,
                        source.DisplayName,
                        source.CategoryId,
                        // #183: parallel family list (nullable for legacy rows)
                        source.TypeFamilyNames is not null && i < source.TypeFamilyNames.Count
                            ? source.TypeFamilyNames[i]
                            : null,
                        // #190 (ADR-064): parallel family-key list
                        source.TypeFamilyKeys is not null && i < source.TypeFamilyKeys.Count
                            ? source.TypeFamilyKeys[i]
                            : null))
                    .ToList()
            };
        }, ct);
    }

    public Task<FamilyBatchImportItem?> StageLoadableAsync(FamilyBatchImportItem item, CancellationToken ct)
    {
        // ADR-066 (E2): "nested://" rows are shared-nested dependencies whose
        // document is held open under that synthetic key (re-opened from the
        // parent's family document) — they stage through the same
        // held-document SaveAs path as "loadable://" rows.
        var isLoadableScheme =
            item.FilePath.StartsWith("loadable://", StringComparison.OrdinalIgnoreCase)
            || item.FilePath.StartsWith("nested://", StringComparison.OrdinalIgnoreCase);
        if (item.Source is not FamilyImportSource.LoadableSource || !isLoadableScheme)
        {
            return Task.FromResult<FamilyBatchImportItem?>(item);
        }

        // Issue #126 / ADR-041: MakeActive imports NO file — only the
        // active-version pointer is switched. Staging would write an
        // orphan managed .rfa no catalog row references.
        if (item.Action == FamilyBatchImportAction.MakeActive)
        {
            return Task.FromResult<FamilyBatchImportItem?>(item);
        }

        return _awaitableEvent.RaiseAsync(_ =>
        {
            var source = (FamilyImportSource.LoadableSource)item.Source!;
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is null)
            {
                SmartConLogger.Warn(
                    "Active document is null — cannot stage loadable family " +
                    "[Action: откройте .rvt проект в Revit, затем повторите команду]");
                return (FamilyBatchImportItem?)null;
            }

            var managedRfaPath = ResolveManagedPath(item, source.FamilyName, ".rfa", out var _);
            if (string.IsNullOrEmpty(managedRfaPath))
            {
                SmartConLogger.Warn(
                    $"Cannot compute managed path for '{source.FamilyName}' — skipping " +
                    "[Action: проверьте, что активная БД каталога выбрана и доступна для записи]");
                return (FamilyBatchImportItem?)null;
            }

            var info = new LoadableFamilyInfo(
                source.FamilyName, source.FamilyUniqueId, source.CategoryName, item.TypeCount ?? 0);

            string? rfaPath;
            try
            {
                rfaPath = StageLoadableFamilyFromProject(activeDoc, info, managedRfaPath!, item.FilePath);
            }
            catch (Exception ex)
            {
                SmartConLogger.Error(
                    $"StageLoadableFamilyFromProject threw for '{source.FamilyName}': {ex.GetType().Name}: {ex.Message} " +
                    "[Action: verify the family is still loaded in the active project]");
                return (FamilyBatchImportItem?)null;
            }

            if (string.IsNullOrEmpty(rfaPath) || !File.Exists(rfaPath))
            {
                SmartConLogger.Warn(
                    $"StageLoadableFamilyFromProject returned empty/missing file for '{source.FamilyName}' " +
                    "[Action: see prior log lines from the staging helper for the underlying cause]");
                return (FamilyBatchImportItem?)null;
            }

            SmartConLogger.Info($"Staged loadable family '{source.FamilyName}' -> '{rfaPath}'");
            return item with { FilePath = rfaPath! };
        }, ct);
    }

    private string? ResolveManagedPath(
        FamilyBatchImportItem item, string displayName, string extension,
        out string? generatedCatalogItemId)
    {
        generatedCatalogItemId = null;
        if (item.Action == FamilyBatchImportAction.OverwriteCurrent
            && !string.IsNullOrEmpty(item.ExistingCatalogItemId)
            && !string.IsNullOrEmpty(item.ExistingVersionLabel))
        {
            var path = _importService.ComputeManagedFilePath(
                item.ExistingCatalogItemId!,
                item.ExistingVersionLabel!,
                SafeFileName.SanitizeFileName(displayName),
                extension);
            if (string.IsNullOrEmpty(path))
            {
                SmartConLogger.Warn(
                    $"OverwriteCurrent staging for '{displayName}': ComputeManagedFilePath returned null " +
                    "[Action: check active catalog DB is selected and pathResolver is configured]");
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path!, File.GetAttributes(path!) & ~FileAttributes.ReadOnly);
            }
            return path;
        }

        if (!string.IsNullOrEmpty(item.PrecomputedManagedPath))
        {
            return item.PrecomputedManagedPath;
        }

        var dbRoot = _databaseManager.GetActiveDatabasePath();
        if (string.IsNullOrEmpty(dbRoot)) return null;
        // #188 (review M3): this fallback allocates the catalog item id — it
        // must reach the mini-project marker, otherwise the ES link
        // id↔file is broken (file under {random-guid}, empty marker id).
        generatedCatalogItemId = Guid.NewGuid().ToString("N");
        var versionDir = Path.Combine(dbRoot, "files", generatedCatalogItemId, "v1");
        var safeName = SafeFileName.SanitizeFileName(SafeFileName.GetBaseName(displayName));
        if (string.IsNullOrEmpty(safeName)) safeName = "Family";
        return Path.Combine(versionDir, safeName + extension);
    }

    private string? StageLoadableFamilyFromProject(
        Document activeDoc, LoadableFamilyInfo info, string managedRfaPath, string? sourcePath)
    {
        var parent = Path.GetDirectoryName(managedRfaPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (File.Exists(managedRfaPath))
        {
            File.SetAttributes(managedRfaPath, File.GetAttributes(managedRfaPath) & ~FileAttributes.ReadOnly);
            File.Delete(managedRfaPath);
        }

        var heldDoc = sourcePath is not null
            ? _preparationService.GetOpenedDocument(sourcePath)
            : null;

        if (heldDoc is not null)
        {
            try
            {
                heldDoc.SaveAs(managedRfaPath, new SaveAsOptions { OverwriteExistingFile = true });
                File.SetAttributes(managedRfaPath, File.GetAttributes(managedRfaPath) | FileAttributes.ReadOnly);
                if (sourcePath is not null) _preparationService.CloseAndRelease(sourcePath);
                SmartConLogger.Debug($"Staged '{info.FamilyName}' from held doc → '{managedRfaPath}'");
                return managedRfaPath;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"SaveAs from held doc failed for '{info.FamilyName}': {ex.Message} — falling back to EditFamily [Action: проверьте логи Revit]");
                if (sourcePath is not null) _preparationService.CloseAndRelease(sourcePath);
            }
        }

        var family = activeDoc.GetElement(info.FamilyUniqueId) as Family;
        if (family is null)
        {
            // For "nested://" rows the UniqueId is scoped to the parent's
            // family document — there is no project-side fallback at all.
            var hint = sourcePath is not null && sourcePath.StartsWith("nested://", StringComparison.OrdinalIgnoreCase)
                ? "вложенное семейство переоткрывается только из held-open документа родителя — проектного fallback нет"
                : "убедитесь, что семейство размещено в активном проекте";
            SmartConLogger.Warn(
                $"Family '{info.FamilyName}' (uid='{info.FamilyUniqueId}') not found in active project " +
                $"[Action: {hint}]");
            return null;
        }
        if (family.IsInPlace)
        {
            SmartConLogger.Warn($"Skipping in-place family '{info.FamilyName}' [Action: in-place семейства не поддерживаются каталогом — пересоздайте его как загружаемое семейство и импортируйте снова]");
            return null;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = activeDoc.EditFamily(family);
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
            {
                SmartConLogger.Warn(
                    $"EditFamily returned null/non-family for '{info.FamilyName}' " +
                    "[Action: проверьте, что семейство валидно и не заблокировано другим процессом]");
                return null;
            }

            familyDoc.SaveAs(managedRfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            File.SetAttributes(managedRfaPath, File.GetAttributes(managedRfaPath) | FileAttributes.ReadOnly);
            SmartConLogger.Debug($"Staged '{info.FamilyName}' → '{managedRfaPath}'");
            return managedRfaPath;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to stage '{info.FamilyName}': {ex.Message} [Action: проверьте логи Revit и повторите импорт]");
            return null;
        }
        finally
        {
            if (familyDoc is not null)
            {
                try { familyDoc.Close(false); } catch { }
                try { Marshal.ReleaseComObject(familyDoc); } catch { }
            }
        }
    }
}
