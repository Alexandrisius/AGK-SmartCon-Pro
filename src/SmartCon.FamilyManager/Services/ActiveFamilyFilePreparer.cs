using System.IO;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Implementation of <see cref="IActiveFamilyFilePreparer"/>. Saves the
/// active family document to a temp staging folder and copies the
/// Type Catalog (.txt) sidecar next to the saved .rfa so that the
/// downstream import pipeline can pick it up via
/// <c>Path.ChangeExtension(tempRfaPath, ".txt")</c>.
/// All Revit API access happens inside
/// <see cref="IFamilyManagerAwaitableEvent"/> callbacks on the Revit
/// UI thread (I-01).
/// </summary>
internal sealed class ActiveFamilyFilePreparer : IActiveFamilyFilePreparer
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IFamilySidecarLocator _sidecarLocator;

    public ActiveFamilyFilePreparer(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IFamilySidecarLocator sidecarLocator)
    {
        _awaitableEvent = awaitableEvent
            ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _sidecarLocator = sidecarLocator
            ?? throw new ArgumentNullException(nameof(sidecarLocator));
    }

    public async Task<ActiveFamilyPreparationResult?> PrepareActiveFamilyAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("ActivePrep",
            ("Method", "PrepareActiveFamilyAsync"));
        var sessionStart = DateTime.Now;
        SmartConLogger.LogSessionStart("ActiveFamilyFilePreparer.PrepareActiveFamilyAsync");
        try
        {

        // Phase 1 (Revit UI thread): read active doc state, SaveAs, find original .txt.
        // We do NOT perform async I/O on the UI thread — the actual sidecar copy
        // runs on a thread-pool continuation below (Phase 2).
        var phase1 = await _awaitableEvent.RaiseAsync<Phase1Result?>(obj =>
        {
            using var _uiScope = SmartConLogger.BeginScope("ActivePrep",
                ("Method", "PrepareActiveFamilyAsync.Phase1"),
                ("Thread", "RevitUI"));
            try
            {
                var uiApp = (UIApplication)obj;
                var activeDoc = uiApp.ActiveUIDocument?.Document;
                if (activeDoc is null)
                {
                    SmartConLogger.Warn("No active document — returning null");
                    return null;
                }

                SmartConLogger.Info(
                    $"Active document: title='{activeDoc.Title}', " +
                    $"isFamily={activeDoc.IsFamilyDocument}, " +
                    $"pathName='{activeDoc.PathName}'");

                if (!activeDoc.IsFamilyDocument)
                {
                    SmartConLogger.Warn(
                        "Active document is NOT a family — caller should use the project flow");
                    return null;
                }

                // IMPORTANT: capture PathName BEFORE SaveAs — Document.SaveAs(path)
                // replaces PathName with the new path, so reading it afterwards
                // returns the temp staging path instead of the user's original file.
                var originalRfaPath = string.IsNullOrEmpty(activeDoc.PathName) ? null : activeDoc.PathName;
                SmartConLogger.Debug(
                    originalRfaPath is null
                        ? "originalRfaPath captured: <untitled>"
                        : $"originalRfaPath captured: '{originalRfaPath}'");

                var tempDir = Path.Combine(Path.GetTempPath(), "SmartCon", "FMLoad", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);
                SmartConLogger.Debug($"Created temp dir: {tempDir}");

                // Prefer originalRfaPath (captured before SaveAs) because its
                // filename preserves dots used as type separators
                // (e.g. "BP_A0307_ITAP_ART.162_Амер угловая.rfa"). activeDoc.Title
                // returns only Family.Name which Revit truncates at the first dot.
                var sourceName = !string.IsNullOrEmpty(originalRfaPath)
                    ? SafeFileName.GetBaseName(originalRfaPath)
                    : SafeFileName.GetBaseName(activeDoc.Title);
                if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "Family";
                foreach (var c in Path.GetInvalidFileNameChars())
                {
                    sourceName = sourceName.Replace(c, '_');
                }
                var tempRfaPath = Path.Combine(tempDir, sourceName + ".rfa");

                SmartConLogger.Info($"Calling activeDoc.SaveAs('{tempRfaPath}')");
                activeDoc.SaveAs(tempRfaPath);
                SmartConLogger.Info(
                    $"SaveAs OK. tempRfa='{tempRfaPath}', " +
                    $"size={new FileInfo(tempRfaPath).Length} bytes");

                // Sidecar lookup is pure I/O on the FS — safe on the UI thread
                // because it is bounded by a single directory enumeration and
                // returns immediately on a missing file. We deliberately
                // do NOT do the file copy here.
                string? originalTxtPath = originalRfaPath is null
                    ? null
                    : _sidecarLocator.FindSidecarPath(originalRfaPath);

                if (originalTxtPath is not null)
                {
                    SmartConLogger.Info($"Original sidecar found: '{originalTxtPath}'");
                }
                else
                {
                    SmartConLogger.Info(
                        originalRfaPath is null
                            ? "activeDoc.PathName is empty (untitled family) — no original to look for a sidecar next to"
                            : $"No .txt sidecar found next to original '{originalRfaPath}' — family has no Type Catalog");
                }

                return new Phase1Result(tempRfaPath, tempDir, originalRfaPath, originalTxtPath);
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"EXCEPTION during phase 1 (SaveAs/lookup): {ex}");
                throw;
            }
        }, ct).ConfigureAwait(false);

        if (phase1 is null)
        {
            SmartConLogger.Info("Phase 1 returned null — propagating null result");
            return null;
        }

        // Phase 2 (thread-pool): copy the sidecar. May be slow if Revit still
        // holds a transient handle on the original .txt; the locator retries.
        string? tempTxtPath = null;
        if (!string.IsNullOrEmpty(phase1.OriginalTxtPath))
        {
            SmartConLogger.Info(
                $"Phase 2: copying sidecar from '{phase1.OriginalTxtPath}' to '{phase1.TempDir}'");
            tempTxtPath = await _sidecarLocator
                .CopySidecarAsync(phase1.OriginalTxtPath!, phase1.TempDir, ct)
                .ConfigureAwait(false);
            if (tempTxtPath is not null)
            {
                SmartConLogger.Info(
                    $"Sidecar copied to temp: '{tempTxtPath}', " +
                    $"size={new FileInfo(tempTxtPath).Length} bytes");
            }
            else
            {
                SmartConLogger.Warn(
                    $"Sidecar copy FAILED (source='{phase1.OriginalTxtPath}'). " +
                    "Type Catalog will be missing from import.");
            }
        }

        var result = new ActiveFamilyPreparationResult(
            TempRfaPath: phase1.TempRfaPath,
            TempTxtPath: tempTxtPath,
            OriginalRfaPath: phase1.OriginalRfaPath,
            OriginalTxtPath: phase1.OriginalTxtPath);

        SmartConLogger.Info(
            $"Final result: tempRfa='{result.TempRfaPath}', " +
            $"tempTxt='{result.TempTxtPath ?? "<none>"}', " +
            $"originalRfa='{result.OriginalRfaPath ?? "<untitled>"}', " +
            $"originalTxt='{result.OriginalTxtPath ?? "<none>"}'");

        SmartConLogger.LogSessionEnd("ActiveFamilyFilePreparer.PrepareActiveFamilyAsync", sessionStart);
        return result;
        }
        catch
        {
            SmartConLogger.LogSessionEnd("ActiveFamilyFilePreparer.PrepareActiveFamilyAsync", sessionStart);
            throw;
        }
    }

    private sealed record Phase1Result(
        string TempRfaPath,
        string TempDir,
        string? OriginalRfaPath,
        string? OriginalTxtPath);
}
