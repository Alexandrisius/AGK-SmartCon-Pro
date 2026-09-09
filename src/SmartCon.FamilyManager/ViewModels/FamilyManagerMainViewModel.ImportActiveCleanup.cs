using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.UI;
using SmartCon.UI.Behaviors;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// Issue #186: after a successful "Импорт активного файла" run closes the
    /// reference mini-project WITHOUT saving and returns focus to the user's
    /// work project — consistent with the .rfa flow
    /// (<see cref="CloseFamilyDocumentAsync"/>).
    /// </summary>
    /// <remarks>
    /// Safety contract (#188):
    /// <list type="bullet">
    /// <item>Only a document carrying the mini-project ES marker is eligible —
    ///       an unmarked document (the user's real work project) is NEVER
    ///       closed;</item>
    /// <item>closing is always <c>Close(false)</c> — saving would overwrite the
    ///       read-only reference version on disk (I-16);</item>
    /// <item><c>Document.Close</c> is forbidden on the ACTIVE document, so
    ///       focus first moves to a work project
    ///       (<see cref="WorkProjectSelector"/>); when none is open, a blank
    ///       project is created — <c>PostableCommand.Close</c> is deliberately
    ///       NOT used because its "Save changes?" prompt risks overwriting the
    ///       reference;</item>
    /// <item>"Импорт выделенных элементов" does not call this method — the
    ///       mini-project stays open.</item>
    /// </list>
    /// </remarks>
    private async Task CloseMiniProjectAfterImportAsync(string capturedMiniProjectPath)
    {
        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", nameof(CloseMiniProjectAfterImportAsync)),
            ("File", System.IO.Path.GetFileName(capturedMiniProjectPath)));
        try
        {
            await _awaitableEvent.RaiseAsync(obj =>
            {
                var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                var app = uiApp.Application;

                Document? capturedDoc = null;
                var openDocs = new List<OpenDocumentInfo>();
                foreach (Document d in app.Documents)
                {
                    // L1: the path fallback keeps legacy unmarked mini-projects
                    // out of the focus candidates as well (ES marker is the
                    // primary check, path pattern covers pre-#188 files).
                    var isMini = _miniProjectMarker.IsMiniProject(d)
                        || MiniProjectPathPattern.IsMiniProjectPath(d.PathName);
                    openDocs.Add(new OpenDocumentInfo(
                        d.PathName ?? string.Empty, d.IsFamilyDocument, d.IsLinked, isMini));
                    if (string.Equals(d.PathName, capturedMiniProjectPath, StringComparison.OrdinalIgnoreCase))
                    {
                        capturedDoc = d;
                    }
                }

                if (capturedDoc is null)
                {
                    SmartConLogger.Debug("Mini-project document not found among open documents (already closed?)");
                    return;
                }

                // #188: NEVER close a document that is not a marked SmartCon
                // mini-project — that would destroy unsaved work in the user's
                // real project. Two factors are required (adversarial review
                // M1): the ES marker AND the managed-storage path. A fork
                // (mini-project SaveAs'd to a user location as the seed of a
                // new work project) keeps the ES marker but leaves managed
                // storage — closing it would lose unsaved user work.
                if (!_miniProjectMarker.IsMiniProject(capturedDoc)
                    || !MiniProjectPathPattern.IsMiniProjectPath(capturedDoc.PathName))
                {
                    SmartConLogger.Info(
                        "Active document is not a marked managed-storage mini-project " +
                        "(no ES marker or outside managed storage) — leaving it open (work-project protection)");
                    return;
                }

                var activePath = uiApp.ActiveUIDocument?.Document?.PathName;
                if (string.Equals(activePath, capturedMiniProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    var workPath = WorkProjectSelector.SelectWorkProjectPath(openDocs, capturedMiniProjectPath);
                    if (workPath is not null)
                    {
                        try
                        {
                            uiApp.OpenAndActivateDocument(workPath);
                        }
                        catch (Exception activateEx)
                        {
                            SmartConLogger.Warn(
                                $"Activate work project failed: {activateEx.Message} " +
                                "[Action: переключитесь на рабочий проект в Revit вручную и закройте мини-проект без сохранения]");
                            return;
                        }
                    }
                    else
                    {
                        // No work project open: Document.Close is forbidden on
                        // the active document and PostableCommand.Close would
                        // ask "Save changes?" — a blank project takes focus so
                        // the reference can be closed without saving.
                        try
                        {
                            app.NewProjectDocument(UnitSystem.Metric);
                        }
                        catch (Exception newEx)
                        {
                            SmartConLogger.Warn(
                                $"NewProjectDocument failed: {newEx.Message} " +
                                "[Action: закройте мини-проект вручную БЕЗ сохранения]");
                            return;
                        }
                    }
                }

                try
                {
                    capturedDoc.Close(false);
                    SmartConLogger.Info("Reference mini-project closed without saving");
                }
                catch (Exception closeEx)
                {
                    SmartConLogger.Warn(
                        $"Mini-project close failed: {closeEx.Message} " +
                        "[Action: закройте мини-проект вручную БЕЗ сохранения]");
                }
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"CloseMiniProjectAfterImportAsync failed: {ex.Message} " +
                "[Action: закройте мини-проект вручную БЕЗ сохранения — каталог уже содержит импортированную версию]");
        }
    }

    /// <summary>
    /// Switches focus back to the project (if one was open) and closes the
    /// family document that was just imported. Runs on the Revit UI thread
    /// via the awaitable external event. Tolerates missing documents
    /// gracefully — Revit may have already closed them.
    /// </summary>
    /// <remarks>
    /// Behaviour:
    /// <list type="bullet">
    /// <item>Project was open before Edit Family → switch focus to project, then close the family</item>
    /// <item>Only a family was open → post the Close command (Revit closes the active doc)</item>
    /// <item>Family already closed by user → no-op</item>
    /// </list>
    /// We pass the post-SaveAs managed path so we close exactly the document
    /// that was just written, regardless of whether SaveAs switched focus.
    /// </remarks>
    private async Task CloseFamilyDocumentAsync(string capturedFamilyPath)
    {
        using var _ = SmartConLogger.BeginScope("FMImport",
            ("Method", "CloseFamilyDocumentAsync"));
        try
        {
            await _awaitableEvent.RaiseAsync(obj =>
            {
                using var _uiScope = SmartConLogger.BeginScope("FMImport",
                    ("Method", "CloseFamilyDocumentAsync"),
                    ("Thread", "RevitUI"));
                var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                var app = uiApp.Application;
                var activeBeforeSwitch = uiApp.ActiveUIDocument?.Document?.PathName;

                var projectDoc = app.Documents.Cast<Document>()
                    .FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked
                        && !string.IsNullOrEmpty(d.PathName)
                        && d.PathName != activeBeforeSwitch);

                if (projectDoc != null)
                {
                    try
                    {
                        uiApp.OpenAndActivateDocument(projectDoc.PathName);
                    }
                    catch (Exception activateEx)
                    {
                        SmartConLogger.Warn(
                            $"Activate project failed: {activateEx.Message} [Action: переключитесь на проект в Revit вручную]");
                        try
                        {
                            var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                            uiApp.PostCommand(closeCmd);
                        }
                        catch { }
                    }
                }
                else
                {
                    try
                    {
                        var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                        uiApp.PostCommand(closeCmd);
                        SmartConLogger.Info(
                            "No project to switch to — posted Close command");
                    }
                    catch (Exception postEx)
                    {
                        SmartConLogger.Warn(
                            $"PostCommand Close failed: {postEx.Message} [Action: закройте активный документ в Revit вручную]");
                    }
                }

                var activeAfterSwitch = uiApp.ActiveUIDocument?.Document?.PathName;
                if (!string.IsNullOrEmpty(capturedFamilyPath)
                    && !string.Equals(activeAfterSwitch, capturedFamilyPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var docToClose = app.Documents.Cast<Document>()
                            .FirstOrDefault(d => string.Equals(
                                d.PathName, capturedFamilyPath, StringComparison.OrdinalIgnoreCase));
                        if (docToClose != null && !docToClose.IsLinked)
                        {
                            docToClose.Close(false);
                            SmartConLogger.Debug(
                                $"Closed family file: {capturedFamilyPath}");
                        }
                        else
                        {
                            SmartConLogger.Debug(
                                "Family document not found in app.Documents (already closed?)");
                        }
                    }
                    catch (Exception closeEx)
                    {
                        SmartConLogger.Info(
                            $"Family close skipped: {closeEx.Message}");
                    }
                }
                else
                {
                    SmartConLogger.Debug(
                        $"Active document switched to '{activeAfterSwitch}' — no need to close family");
                }
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"CloseFamilyDocumentAsync failed: {ex.Message} [Action: переключитесь на нужный документ в Revit вручную, каталог уже содержит импортированную запись]");
        }
    }
}
