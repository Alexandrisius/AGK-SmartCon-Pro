using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Serializes a <see cref="FamilyGeometryPreview"/> to a GLB (binary glTF 2.0)
/// file. Pure C# implementation (SharpGLTF.Toolkit) — no Revit API, no WPF
/// (I-09), unit-testable without a Revit process.
/// </summary>
public interface IGlbWriter
{
    /// <summary>
    /// Writes the preview to a GLB file at <paramref name="outputPath"/>.
    /// Creates the parent directory if it does not exist. Overwrites the
    /// file if it already exists.
    /// </summary>
    /// <param name="preview">Geometry to serialize. Caller ensures
    /// <see cref="FamilyGeometryPreview.IsEmpty"/> is <c>false</c>.</param>
    /// <param name="outputPath">Absolute target file path, typically
    /// <c>{tempdir}/{guid}.glb</c> before the asset service copies it
    /// into managed storage.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> on success; <c>false</c> on failure (logged
    /// internally, not rethrown — pipeline treats <c>false</c> as "skip
    /// asset registration").</returns>
    Task<bool> WriteAsync(
        FamilyGeometryPreview preview,
        string outputPath,
        CancellationToken ct = default);
}
