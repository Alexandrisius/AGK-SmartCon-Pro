#if NET8_0_OR_GREATER
using System.IO;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
#endif
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Geometry;

/// <summary>
/// Pure-C# GLB writer (SharpGLTF.Toolkit). Serializes a
/// <see cref="FamilyGeometryPreview"/> into a binary glTF 2.0 file
/// that can be loaded by <see cref="GlbSceneLoader"/> for display in
/// <c>HelixToolkit.Wpf.SharpDX.Viewport3DX</c>.
/// </summary>
/// <remarks>
/// <b>Multi-version:</b> SharpGLTF.Toolkit 1.0.4 transitively requires
/// System.Text.Json 9.x which breaks net48 (CS1739 error AppendFormatted).
/// For net8.0-windows (Revit 2025+) the full SharpGLTF implementation runs;
/// for net48 (Revit 2019-2024) the stub logs a Warn and returns false —
/// the pipeline then skips asset registration. 3D preview unavailable on net48.
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
#if NET8_0_OR_GREATER
        return WriteCoreNet8(preview, outputPath, ct);
#else
        SmartConLogger.Warn(
            "GLB write is unsupported on net48 — SharpGLTF.Toolkit requires System.Text.Json 9.x " +
            "which breaks net48 interpolation. [Action: 3D preview unavailable on Revit 2019-2024; " +
            "use Revit 2025+ (net8.0) to generate GLB previews]");
        return Task.FromResult(false);
#endif
    }

#if NET8_0_OR_GREATER
    private static async Task<bool> WriteCoreNet8(FamilyGeometryPreview preview, string outputPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(outputPath);

        if (preview.IsEmpty)
        {
            SmartConLogger.Warn(
                $"GLB write skipped: preview is empty (CatalogItemId={preview.CatalogItemId}, " +
                $"VersionLabel={preview.VersionLabel}). [Action: ensure the .rfa has visible 3D geometry; " +
                $"if not, the 3D preview tab will show 'no geometry' which is expected for annotations]");
            return false;
        }

        var ok = WriteCore(preview, outputPath);
        return await Task.FromResult(ok).ConfigureAwait(false);
    }

    private static bool WriteCore(FamilyGeometryPreview preview, string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var scene = new SceneBuilder(preview.FamilyName);

            foreach (var mesh in preview.Meshes)
            {
                if (mesh.IsEmpty) continue;
                AddMeshToScene(scene, mesh);
            }

            var model = scene.ToGltf2();
            model.SaveGLB(outputPath);

            SmartConLogger.Info(
                $"GLB written: {Path.GetFileName(outputPath)}, " +
                $"{preview.Meshes.Count} meshes, {preview.TotalVertexCount} verts, " +
                $"{preview.TotalTriangleCount} tris");

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

    /// <summary>
    /// Revit uses a Z-up coordinate system (X right, Y forward, Z up).
    /// glTF 2.0 specification defines Y-up (X right, Y up, Z toward viewer).
    /// This rotation (-90° around X axis) maps Revit Z-up to glTF Y-up:
    /// (x, y, z) → (x, z, -y), preserving winding order (det=1, proper rotation).
    /// Without this, models appear lying flat — the front face points up instead
    /// of toward the viewer. SharpGLTF's <c>AddRigidMesh</c> applies the matrix
    /// to both positions AND normals (via inverse-transpose), so lighting stays
    /// correct.
    /// </summary>
    private static readonly Matrix4x4 RevitToGltf = Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    private static void AddMeshToScene(SceneBuilder scene, MeshData mesh)
    {
        SmartConLogger.Debug(
            $"  GLB mesh '{mesh.NodeName}': {mesh.VertexCount} verts, " +
            $"{mesh.TriangleCount} tris, normals={(mesh.Normals is not null ? "yes" : "no")}");

        var material = new MaterialBuilder($"mat_{mesh.NodeName}")
            .WithDoubleSide(false)
            .WithMetallicRoughnessShader()
            .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, mesh.DiffuseColor);

        if (mesh.Normals is null)
        {
            AddPositionOnlyMesh(scene, mesh, material);
        }
        else
        {
            AddPositionNormalMesh(scene, mesh, material);
        }
    }

    private static void AddPositionOnlyMesh(SceneBuilder scene, MeshData mesh, MaterialBuilder material)
    {
        var mb = new MeshBuilder<VertexPosition>("mesh_" + mesh.NodeName);
        var prim = mb.UsePrimitive(material);
        var positions = mesh.Positions;
        var indices = mesh.Indices;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int i0 = indices[i] * 3;
            int i1 = indices[i + 1] * 3;
            int i2 = indices[i + 2] * 3;
            var v0 = new VertexPosition(positions[i0], positions[i0 + 1], positions[i0 + 2]);
            var v1 = new VertexPosition(positions[i1], positions[i1 + 1], positions[i1 + 2]);
            var v2 = new VertexPosition(positions[i2], positions[i2 + 1], positions[i2 + 2]);
            prim.AddTriangle(v0, v1, v2);
        }

        scene.AddRigidMesh(mb, RevitToGltf);
    }

    private static void AddPositionNormalMesh(SceneBuilder scene, MeshData mesh, MaterialBuilder material)
    {
        var mb = new MeshBuilder<VertexPositionNormal>("mesh_" + mesh.NodeName);
        var prim = mb.UsePrimitive(material);
        var positions = mesh.Positions;
        var normals = mesh.Normals!;
        var indices = mesh.Indices;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int i0 = indices[i] * 3;
            int i1 = indices[i + 1] * 3;
            int i2 = indices[i + 2] * 3;
            var v0 = new VertexPositionNormal(
                positions[i0], positions[i0 + 1], positions[i0 + 2],
                normals[i0], normals[i0 + 1], normals[i0 + 2]);
            var v1 = new VertexPositionNormal(
                positions[i1], positions[i1 + 1], positions[i1 + 2],
                normals[i1], normals[i1 + 1], normals[i1 + 2]);
            var v2 = new VertexPositionNormal(
                positions[i2], positions[i2 + 1], positions[i2 + 2],
                normals[i2], normals[i2 + 1], normals[i2 + 2]);
            prim.AddTriangle(v0, v1, v2);
        }

        scene.AddRigidMesh(mb, RevitToGltf);
    }
#endif
}
