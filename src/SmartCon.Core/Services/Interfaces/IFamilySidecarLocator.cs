namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Pure I/O helper for locating and copying Type Catalog (.txt) sidecar
/// files that accompany <c>.rfa</c> family files. No Revit API dependency —
/// fully unit-testable with regular file system fixtures.
///
/// Revit stores the Type Catalog in the same folder as the .rfa with the
/// same basename but <c>.txt</c> extension (e.g. <c>MyFamily.rfa</c> →
/// <c>MyFamily.txt</c>). The lookup is case-insensitive on both the
/// extension and the basename to tolerate Linux/Win file sharing and
/// unusual casing from upstream tools.
/// </summary>
public interface IFamilySidecarLocator
{
    /// <summary>
    /// Finds a Type Catalog sidecar (.txt) that accompanies the given
    /// <paramref name="rfaPath"/>. Returns <c>null</c> if no sidecar is
    /// present or the original file path is empty/invalid.
    /// </summary>
    /// <param name="rfaPath">Absolute path to a .rfa file.</param>
    string? FindSidecarPath(string? rfaPath);

    /// <summary>
    /// Copies the Type Catalog sidecar at <paramref name="sourceTxtPath"/>
    /// into <paramref name="destDir"/>, preserving the original file name.
    /// Creates <paramref name="destDir"/> if it does not exist. Uses a
    /// short retry loop to tolerate transient file locks (Revit may keep
    /// a transient handle on the source file in some workflows).
    /// </summary>
    /// <param name="sourceTxtPath">Absolute path to the source .txt file.</param>
    /// <param name="destDir">Destination directory (will be created if missing).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Absolute path to the copied file, or <c>null</c> if the source does
    /// not exist or could not be copied after all retries.
    /// </returns>
    Task<string?> CopySidecarAsync(string sourceTxtPath, string destDir, CancellationToken ct = default);
}
