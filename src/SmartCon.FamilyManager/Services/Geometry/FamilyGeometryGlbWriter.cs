using System.IO;
using System.Numerics;
using SharpGLTF.Schema2;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Geometry;

/// <summary>
/// Pure-C# GLB writer (SharpGLTF.Core Schema2 low-level API). Serializes a
/// <see cref="FamilyGeometryPreview"/> into a binary glTF 2.0 file that can
/// be loaded by <see cref="GlbSceneLoader"/> for display in
/// <c>HelixToolkit.Wpf.SharpDX.Viewport3DX</c>.
/// </summary>
/// <remarks>
/// <b>ADR-042:</b> Uses SharpGLTF.Core (netstandard2.0) — not SharpGLTF.Toolkit.
/// Toolkit was replaced because it transitively requires System.Text.Json 9.x
/// which breaks net48 (CS1739 AppendFormatted). Core targets netstandard2.0
/// and works on both net8.0-windows (Revit 2025+) and net48 (Revit 2019-2024).
/// <para>
/// The writer uses the low-level Schema2 API directly:
/// <c>ModelRoot.CreateModel</c> → <c>UseBufferView</c> → <c>CreateAccessor</c> →
/// <c>SetVertexData</c>/<c>SetIndexData</c> → <c>CreateMesh</c> →
/// <c>CreatePrimitive</c> → <c>SetVertexAccessor</c>/<c>SetIndexAccessor</c> →
/// <c>SaveGLB</c>. This is the same pipeline that Toolkit's
/// <c>MeshBuilder</c>/<c>SceneBuilder</c> use internally, but without the
/// System.Text.Json dependency.
/// </para>
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

    /// <summary>
    /// Revit uses a Z-up coordinate system (X right, Y forward, Z up).
    /// glTF 2.0 specification defines Y-up (X right, Y up, Z toward viewer).
    /// This rotation (-90° around X axis) maps Revit Z-up to glTF Y-up:
    /// (x, y, z) → (x, z, -y), preserving winding order (det=1, proper rotation).
    /// Applied to the root node's WorldMatrix so all child meshes inherit it.
    /// </summary>
    private static readonly Matrix4x4 RevitToGltf = Matrix4x4.CreateRotationX(-(float)Math.PI / 2f);

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
                $"if not, the 3D preview tab will show 'no geometry' which is expected for annotations]");
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

            var model = ModelRoot.CreateModel();
            var scene = model.UseScene(0);
            var rootNode = scene.CreateNode(preview.FamilyName);
            rootNode.WorldMatrix = RevitToGltf;

            var meshIndex = 0;
            foreach (var mesh in preview.Meshes)
            {
                if (mesh.IsEmpty)
                {
                    SmartConLogger.Debug($"  GLB mesh #{meshIndex} '{mesh.NodeName}' is empty — skipped");
                    meshIndex++;
                    continue;
                }
                AddMeshToScene(model, rootNode, mesh);
                meshIndex++;
            }

            model.SaveGLB(outputPath);

            SmartConLogger.Info(
                $"GLB written: {preview.Meshes.Count} meshes, {preview.TotalVertexCount} verts, " +
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
    /// Creates a glTF mesh with POSITION (+ optional NORMAL) and INDICES accessors,
    /// attaches it to a child node of <paramref name="parentNode"/>.
    /// Each <see cref="MeshData"/> becomes one <see cref="Mesh"/> with one
    /// <see cref="MeshPrimitive"/>.
    /// </summary>
    private static void AddMeshToScene(ModelRoot model, Node parentNode, MeshData mesh)
    {
        SmartConLogger.Debug(
            $"  GLB mesh '{mesh.NodeName}': {mesh.VertexCount} verts, " +
            $"{mesh.TriangleCount} tris, normals={(mesh.Normals is not null ? "yes" : "no")}");

        var material = CreateMaterialWithBaseColor(model, mesh);

        var gltfMesh = model.CreateMesh(mesh.NodeName);
        var primitive = gltfMesh.CreatePrimitive();
        primitive.DrawPrimitiveType = PrimitiveType.TRIANGLES;
        primitive.Material = material;

        var posAccessor = CreatePositionAccessor(model, mesh);
        primitive.SetVertexAccessor("POSITION", posAccessor);

        if (mesh.Normals is not null && mesh.Normals.Length == mesh.Positions.Length)
        {
            var nrmAccessor = CreateNormalAccessor(model, mesh);
            primitive.SetVertexAccessor("NORMAL", nrmAccessor);
        }

        var idxAccessor = CreateIndexAccessor(model, mesh);
        primitive.SetIndexAccessor(idxAccessor);

        var childNode = parentNode.CreateNode();
        childNode.Mesh = gltfMesh;
    }

    /// <summary>
    /// Creates a PBR Metallic Roughness material with the mesh's diffuse color
    /// as the BaseColor factor. Uses Core API: <c>InitializePBRMetallicRoughness</c>
    /// + <c>FindChannel("BaseColor").Value.Color</c> setter.
    /// </summary>
    private static Material CreateMaterialWithBaseColor(ModelRoot model, MeshData mesh)
    {
        var material = model.CreateMaterial($"mat_{mesh.NodeName}");
        material.InitializePBRMetallicRoughness();

        var channel = material.FindChannel("BaseColor");
        if (channel.HasValue)
        {
            var ch = channel.Value;
            ch.Color = mesh.DiffuseColor;
        }
        else
        {
            SmartConLogger.Warn(
                $"Material 'mat_{mesh.NodeName}': FindChannel('BaseColor') returned null " +
                "[Action: material will use default white BaseColor; verify SharpGLTF.Core API version]");
        }

        return material;
    }

    /// <summary>
    /// Creates an accessor for POSITION vertex data (VEC3 FLOAT).
    /// Converts <c>float[]</c> to <c>byte[]</c> via <c>Buffer.BlockCopy</c>,
    /// wraps in a BufferView with <see cref="BufferMode.ARRAY_BUFFER"/> target,
    /// then calls <see cref="Accessor.SetVertexData"/>.
    /// </summary>
    private static Accessor CreatePositionAccessor(ModelRoot model, MeshData mesh)
    {
        var positions = mesh.Positions;
        var vertexCount = mesh.VertexCount;
        var byteCount = positions.Length * sizeof(float);

        var bytes = new byte[byteCount];
        System.Buffer.BlockCopy(positions, 0, bytes, 0, byteCount);

        var bufferView = model.UseBufferView(bytes, 0, byteCount, 0, BufferMode.ARRAY_BUFFER);

        var accessor = model.CreateAccessor("POSITION_" + mesh.NodeName);
        accessor.SetVertexData(bufferView, 0, vertexCount, DimensionType.VEC3, EncodingType.FLOAT, false);
        return accessor;
    }

    /// <summary>
    /// Creates an accessor for NORMAL vertex data (VEC3 FLOAT).
    /// Same pattern as <see cref="CreatePositionAccessor"/> but only called
    /// when <see cref="MeshData.Normals"/> is non-null and matches positions length.
    /// </summary>
    private static Accessor CreateNormalAccessor(ModelRoot model, MeshData mesh)
    {
        var normals = mesh.Normals!;
        var vertexCount = mesh.VertexCount;
        var byteCount = normals.Length * sizeof(float);

        var bytes = new byte[byteCount];
        System.Buffer.BlockCopy(normals, 0, bytes, 0, byteCount);

        var bufferView = model.UseBufferView(bytes, 0, byteCount, 0, BufferMode.ARRAY_BUFFER);

        var accessor = model.CreateAccessor("NORMAL_" + mesh.NodeName);
        accessor.SetVertexData(bufferView, 0, vertexCount, DimensionType.VEC3, EncodingType.FLOAT, false);
        return accessor;
    }

    /// <summary>
    /// Creates an accessor for triangle indices (SCALAR UNSIGNED_INT).
    /// glTF 2.0 spec requires index encoding as UNSIGNED_BYTE, UNSIGNED_SHORT,
    /// or UNSIGNED_INT. We use UNSIGNED_INT (4 bytes per index) for uniformity
    /// — works for meshes with >65535 vertices. BufferView target is
    /// <see cref="BufferMode.ELEMENT_ARRAY_BUFFER"/>.
    /// </summary>
    private static Accessor CreateIndexAccessor(ModelRoot model, MeshData mesh)
    {
        var indices = mesh.Indices;
        var byteCount = indices.Length * sizeof(uint);

        var bytes = new byte[byteCount];
        System.Buffer.BlockCopy(indices, 0, bytes, 0, byteCount);

        var bufferView = model.UseBufferView(bytes, 0, byteCount, 0, BufferMode.ELEMENT_ARRAY_BUFFER);

        var accessor = model.CreateAccessor("INDICES_" + mesh.NodeName);
        accessor.SetIndexData(bufferView, 0, indices.Length, IndexEncodingType.UNSIGNED_INT);
        return accessor;
    }
}
