namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Removes the temp staging folders created by the "Import Active File"
/// command once an import has finished (success, failure or cancel).
/// Replaces an ad-hoc static helper in the ViewModel layer.
/// </summary>
public interface IActiveImportCleanupService
{
    /// <summary>
    /// Deletes all per-import staging subfolders under
    /// <c>%TEMP%\SmartCon</c>. Safe to call multiple times; missing
    /// folders are ignored.
    /// </summary>
    Task CleanupAfterImportAsync(CancellationToken ct = default);
}
