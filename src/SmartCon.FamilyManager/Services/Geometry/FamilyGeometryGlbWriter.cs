using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Geometry.Gltf;

namespace SmartCon.FamilyManager.Services.Geometry;

/// <summary>
/// Pure-C# GLB 2.0 writer. Serializes a <see cref="FamilyGeometryPreview"/> into
/// a binary glTF 2.0 file that can be loaded by <see cref="GlbSceneLoader"/> for
/// display in <c>HelixToolkit.Wpf.SharpDX.Viewport3DX</c>.
/// </summary>
/// <remarks>
/// <b>ADR-042 / Issue #129:</b> Uses a custom, zero-dependency implementation
/// instead of SharpGLTF.Core. SharpGLTF.Core 1.0.3 internally depends on
/// System.Text.Json, which crashes in Revit 2021's shared AppDomain
/// (MissingMethodException on System.Text.Json.JsonWriterOptions.set_Encoder).
/// The custom writer uses manual JSON serialization and GLB binary container
/// construction, with no dependency on System.Text.Json or Newtonsoft.Json.
/// </remarks>
public sealed class FamilyGeometryGlbWriter : IGlbWriter
{
    /// <summary>
    /// Prefix used in <c>family_assets.description</c> to identify GLB
    /// assets that were auto-extracted at import time (vs user-uploaded
    /// Model3D files). The FamilyPropertiesViewModel uses this prefix to
    /// split <c>Model3DAssets</c> into the 3D viewer (this prefix) and the
    /// regular asset list in the Content tab (everything else).
    /// </summary>
    public const string AutoExtractedAssetDescriptionPrefix = "auto-extracted-preview:";

    public Task<bool> WriteAsync(
        FamilyGeometryPreview preview,
        string outputPath,
        CancellationToken ct = default)
    {
        if (preview is null) throw new ArgumentNullException(nameof(preview));
        if (outputPath is null) throw new ArgumentNullException(nameof(outputPath));

        using var _scope = SmartConLogger.BeginScope("GlbWrite",
            ("Method", nameof(WriteAsync)),
            ("FamilyName", preview.FamilyName),
            ("FilePath", Path.GetFileName(outputPath)));

        if (preview.IsEmpty)
        {
            SmartConLogger.Warn(
                $"GLB write skipped: preview is empty (CatalogItemId={preview.CatalogItemId}, " +
                $"VersionLabel={preview.VersionLabel}). [Action: ensure the .rfa has visible 3D geometry; " +
                "if not, the 3D preview tab will show 'no geometry' which is expected for annotations]");
            return Task.FromResult(false);
        }

        return Task.FromResult(WriteCore(preview, outputPath));
    }

    private static bool WriteCore(FamilyGeometryPreview preview, string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            SmartConLogger.Debug("Building GLB JSON model and binary buffer");
            var (model, buffer) = GltfBufferBuilder.Build(preview);

            SmartConLogger.Debug(
                $"Serializing glTF JSON: {model.Nodes.Count} nodes, {model.Meshes.Count} meshes, " +
                $"{model.Accessors.Count} accessors, {model.Materials.Count} materials");
            var json = GltfJsonSerializer.Serialize(model);

            SmartConLogger.Debug($"Writing GLB container ({buffer.Length} bytes binary + {json.Length} chars JSON)");
            var glbBytes = GltfBinaryWriter.Write(json, buffer);
            File.WriteAllBytes(outputPath, glbBytes);

            SmartConLogger.Info(
                $"GLB written: {preview.Meshes.Count} meshes, {preview.TotalVertexCount} verts, " +
                $"{preview.TotalTriangleCount} tris, {glbBytes.Length} bytes");

            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"GLB write failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: check geometry preview data validity; 3D preview will be skipped for this version]");
            return false;
        }
    }
}
