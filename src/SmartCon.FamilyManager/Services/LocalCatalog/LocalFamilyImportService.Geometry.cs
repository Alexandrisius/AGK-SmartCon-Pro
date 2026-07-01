using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// Partial of <see cref="LocalFamilyImportService"/> holding the
/// <c>ADR-042</c> 3D geometry-preview hook. The hook fires after
/// <c>tx.Commit()</c> in three places (H1 / H2 / H3) and is fully
/// error-tolerant — a GLB preview failure MUST NOT abort the already
/// committed import transaction.
/// </summary>
internal sealed partial class LocalFamilyImportService
{
    /// <summary>
    /// Optional pipeline that extracts 3D geometry from the managed .rfa
    /// and writes a GLB asset. Null in unit-test scenarios where Revit
    /// API is unavailable — the import proceeds without a 3D preview.
    /// </summary>
    private readonly IFamilyGeometryPipeline? _geometryPipeline;

    /// <summary>
    /// Hook invoked right after the import transaction commits in
    /// ImportFileAsync (H1), UpdateFamilyAsync (H2) and
    /// OverwriteCurrentAsync (H3). Delegates to
    /// <see cref="IFamilyGeometryPipeline.RunAsync"/> which internally
    /// marshals Revit API calls through
    /// <c>IFamilyManagerAwaitableEvent.RaiseAsync</c> (I-01).
    /// </summary>
    /// <remarks>
    /// All exceptions are swallowed — geometry preview is a nice-to-have.
    /// The hook is <c>await</c>ed (not fire-and-forget) so the GLB is
    /// guaranteed to be on disk by the time ImportFileAsync returns to
    /// the batch iterator — otherwise a follow-up UI load could race the
    /// pipeline.
    /// </remarks>
    private async Task RunGeometryPipelineHookAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string familyName,
        CancellationToken ct)
    {
        if (_geometryPipeline is null)
        {
            SmartConLogger.Debug(
                $"Geometry pipeline skipped (no pipeline registered): family='{familyName}', v{versionLabel}");
            return;
        }

        try
        {
            await _geometryPipeline.RunAsync(
                managedRfaPath,
                catalogItemId,
                versionId,
                versionLabel,
                familyName,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Pipeline itself already logs internally — this is the last
            // defensive line so an unforeseen exception in the await chain
            // cannot abort the import that already committed.
            SmartConLogger.Warn(
                $"Geometry pipeline hook unhandled exception: {ex.GetType().Name}: {ex.Message} " +
                "[Action: import continues; 3D preview will be unavailable for this version]");
        }
    }

    /// <summary>
    /// Strips the <c>.rfa</c> / <c>.rvt</c> suffix from <paramref name="fileName"/>
    /// to obtain a family-name for the GLB asset's file_name. Returns the
    /// original if no recognized extension is found.
    /// </summary>
    private static string StripFamilyExtension(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "family";
        if (fileName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            return fileName[..^4];
        if (fileName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            return fileName[..^4];
        return Path.GetFileNameWithoutExtension(fileName);
    }
}
