using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IMiniProjectActualizationService"/>
/// (Issue #189). First actualization operation that WRITES into a managed
/// file: opens the staged .rvt, writes the ES marker (#188), saves in
/// place (<see cref="Document.Save()"/> — same path, same version; SaveAs
/// would break <c>family_files.relative_path</c>), deletes the Revit
/// backup files it creates (<c>name.NNNN.rvt</c> — the version folder must
/// hold exactly one .rvt, owner requirement 2026-08-04) and restores the
/// read-only attribute (I-16 engine-level exception, same pattern as the
/// staging SaveAs paths). Marshals to the Revit UI thread via
/// <see cref="IFamilyManagerAwaitableEvent"/> (I-01).
/// </summary>
public sealed class RevitMiniProjectActualizationService : IMiniProjectActualizationService
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IMiniProjectMarker _marker;

    public RevitMiniProjectActualizationService(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IMiniProjectMarker marker)
    {
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _marker = marker ?? throw new ArgumentNullException(nameof(marker));
    }

    public async Task<MiniProjectMarkFileOutcome> MarkManagedFileAsync(
        string absolutePath, string catalogItemId, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(absolutePath);
        using var _scope = SmartConLogger.BeginScope(
            "MiniProjectMarker",
            ("Method", nameof(MarkManagedFileAsync)),
            ("FileName", fileName));

        return await _awaitableEvent
            .RaiseAsync(appObj => MarkOnMainThread(appObj, absolutePath, catalogItemId), ct)
            .ConfigureAwait(false);
    }

    private MiniProjectMarkFileOutcome MarkOnMainThread(
        object appObj, string absolutePath, string catalogItemId)
    {
        if (!File.Exists(absolutePath))
        {
            SmartConLogger.Warn(
                $"Managed file missing: '{absolutePath}' [Action: версия помечена терминальным маркером (-2); " +
                "удалите запись через инструменты базы или восстановите файл]");
            return new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.Missing, 0, "file not found");
        }

        Document? doc = null;
        var readOnlyCleared = false;
        try
        {
            // The awaitable event delivers UIApplication in production; the
            // integration test host (Nice3point, DB-level only) delivers the
            // bare ApplicationServices.Application. The UIApplication unwrap
            // lives in a dedicated method: a static RevitAPIUI reference in
            // THIS method would crash the DB-only test host at JIT time even
            // when the branch is never taken (session AVE — the host cannot
            // load RevitAPIUI).
            var app = appObj as Autodesk.Revit.ApplicationServices.Application
                ?? UnwrapUIApplication(appObj);
            doc = app.OpenDocumentFile(absolutePath);

            if (_marker.IsMiniProject(doc)
                && string.Equals(_marker.ReadCatalogItemId(doc), catalogItemId, StringComparison.Ordinal))
            {
                // Backup hygiene also on the skip path (review m2): a backup
                // whose deletion failed in an earlier run would otherwise
                // survive forever behind the short circuit.
                var staleBackups = DeleteRevitBackups(absolutePath);
                SmartConLogger.Debug("ES marker already present for this catalog item — skipping the rewrite");
                return new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.AlreadyMarked, staleBackups);
            }

            var attributes = File.GetAttributes(absolutePath);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(absolutePath, attributes & ~FileAttributes.ReadOnly);
                readOnlyCleared = true;
            }

            _marker.MarkAsMiniProject(doc, catalogItemId);
            doc.Save();

            var backupsDeleted = DeleteRevitBackups(absolutePath);
            SmartConLogger.Info(
                $"Mini-project marked (item '{catalogItemId}'), saved in place; backups deleted: {backupsDeleted}");
            return new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.Marked, backupsDeleted);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Marking '{Path.GetFileName(absolutePath)}' failed: {ex.Message} " +
                "[Action: версия помечена терминальным маркером пропуска (-1); проверьте, что файл не открыт в другом Revit и доступен на запись]");
            return new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.Failed, 0, ex.Message);
        }
        finally
        {
            if (doc is not null)
            {
                try { doc.Close(false); }
                catch (Exception ex) { SmartConLogger.Debug($"doc.Close skipped: {ex.Message}"); }
                try { Marshal.ReleaseComObject(doc); }
                catch (Exception ex) { SmartConLogger.Debug($"ReleaseComObject skipped: {ex.Message}"); }
            }
            if (readOnlyCleared)
            {
                try
                {
                    File.SetAttributes(absolutePath, File.GetAttributes(absolutePath) | FileAttributes.ReadOnly);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to restore the read-only attribute on '{Path.GetFileName(absolutePath)}': {ex.Message} " +
                        "[Action: выставьте атрибут «Только чтение» вручную — managed storage должен оставаться read-only (I-16)]");
                }
            }
        }
    }

    /// <summary>
    /// Unwraps the production UIApplication to the inner application object.
    /// Kept out of <see cref="MarkOnMainThread"/> on purpose: this method is
    /// JITted only when the production awaitable event actually delivers a
    /// UIApplication — the DB-only integration test host (no RevitAPIUI on
    /// disk) never reaches it.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Autodesk.Revit.ApplicationServices.Application UnwrapUIApplication(object appObj)
    {
        if (appObj is not UIApplication uiApp)
        {
            throw new InvalidOperationException(
                $"Unexpected Revit app context type: {appObj.GetType().FullName}");
        }
        return uiApp.Application;
    }

    /// <summary>
    /// Deletes the Revit backup files (<c>name.NNNN.rvt</c>) that
    /// <see cref="Document.Save()"/> leaves next to the managed file.
    /// Backup deletion failure is a Warn, not a task failure — a stray
    /// backup is hygiene, not data integrity.
    /// </summary>
    private static int DeleteRevitBackups(string absolutePath)
    {
        var directory = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrEmpty(directory)) return 0;
        var baseName = Path.GetFileNameWithoutExtension(absolutePath);

        var deleted = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory, baseName + ".*.rvt"))
        {
            var suffix = Path.GetFileName(candidate).AsSpan(baseName.Length);
            // ".NNNN.rvt" — Revit backup naming for project files.
            if (suffix.Length != 9 || suffix[0] != '.'
                || !int.TryParse(suffix.Slice(1, 4).ToString(), out _))
                continue;
            try
            {
                File.SetAttributes(candidate, FileAttributes.Normal);
                File.Delete(candidate);
                deleted++;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to delete Revit backup '{Path.GetFileName(candidate)}': {ex.Message} " +
                    "[Action: удалите файл вручную — в папке версии должен оставаться один .rvt]");
            }
        }
        if (deleted > 0)
        {
            SmartConLogger.Debug($"Deleted {deleted} Revit backup file(s) next to '{Path.GetFileName(absolutePath)}'");
        }
        return deleted;
    }
}
