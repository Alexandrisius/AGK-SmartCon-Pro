using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Extracts Type parameters from staged system-family .rvt files in a uniform way
/// for both ImportActiveFile and ImportSystemFamily flows. Replaces the duplicated
/// inline extraction logic previously embedded in
/// <c>FamilyManagerMainViewModel.ExtractAttributesFromRvtsAsync</c> and
/// <c>FamilyManagerMainViewModel.ExtractSystemFamilyAttributesAsync</c>.
/// </summary>
public interface ISystemFamilyAttributeExtractor
{
    /// <summary>
    /// Opens each staged .rvt via the awaitable external event, extracts Type
    /// parameters, and persists results to the catalog.
    /// Implementation MUST await all in-flight saves before returning, so the
    /// caller can safely delete temp files (cleanup race condition guard).
    /// </summary>
    Task ExtractAndSaveAsync(
        IReadOnlyList<SystemFamilyExtractionTask> tasks,
        CancellationToken ct = default);
}
