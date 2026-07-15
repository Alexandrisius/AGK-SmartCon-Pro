using System.Numerics;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal static class GltfBufferBuilder
{
    public static (GltfModel Model, byte[] Buffer) Build(FamilyGeometryPreview preview)
    {
        using var _scope = SmartConLogger.BeginScope("GlbBuild",
            ("Method", nameof(Build)),
            ("FamilyName", preview.FamilyName),
            ("MeshCount", preview.Meshes.Count));

        SmartConLogger.Info(
            $"Building GLB buffer: {preview.Meshes.Count} source meshes, " +
            $"{preview.TotalVertexCount} verts, {preview.TotalTriangleCount} tris");

        var asset = new GltfAsset(Generator: "SmartCon FamilyManager");
        var bufferViews = new List<GltfBufferView>();
        var accessors = new List<GltfAccessor>();
        var materials = new List<GltfMaterial>();
        var meshes = new List<GltfMesh>();
        var nodes = new List<GltfNode>();
        var rootChildren = new List<int>();

        nodes.Add(null!);

        int bufferOffset = 0;
        int meshIndex = 0;
        int nonEmptyMeshCount = 0;

        foreach (var mesh in preview.Meshes)
        {
            if (mesh.IsEmpty)
            {
                SmartConLogger.Debug($"Skipping empty mesh '{mesh.NodeName}'");
                meshIndex++;
                continue;
            }

            nonEmptyMeshCount++;
            int positionAccessor = AppendPositionAccessor(mesh, bufferViews, accessors, ref bufferOffset);
            int? normalAccessor = AppendNormalAccessor(mesh, bufferViews, accessors, ref bufferOffset);
            int indexAccessor = AppendIndexAccessor(mesh, bufferViews, accessors, ref bufferOffset);
            int materialIndex = AppendMaterial(mesh, materials);

            var attributes = new GltfPrimitiveAttributes(positionAccessor, normalAccessor);
            var primitive = new GltfPrimitive(attributes, indexAccessor, materialIndex, GltfConstants.ModeTriangles);
            meshes.Add(new GltfMesh(mesh.NodeName, new[] { primitive }));

            nodes.Add(new GltfNode(mesh.NodeName, meshIndex, null, null));
            rootChildren.Add(nodes.Count - 1);

            SmartConLogger.Debug(
                $"Mesh '{mesh.NodeName}' layout: posAcc={positionAccessor}, " +
                $"nrmAcc={(normalAccessor.HasValue ? normalAccessor.Value.ToString() : "none")}, " +
                $"idxAcc={indexAccessor}, mat={materialIndex}, verts={mesh.VertexCount}, tris={mesh.TriangleCount}");

            meshIndex++;
            bufferOffset = Align(bufferOffset, 4);
        }

        nodes[0] = new GltfNode(preview.FamilyName, null, rootChildren, GetRootTransformMatrix());
        var scene = new GltfScene(new[] { 0 });

        var buffer = new GltfBuffer(bufferOffset);
        var model = new GltfModel(
            asset,
            new[] { scene },
            nodes,
            meshes,
            accessors,
            bufferViews,
            new[] { buffer },
            materials);

        var binaryData = BuildBinaryData(preview, bufferOffset);

        SmartConLogger.Info(
            $"GLB buffer built: {nonEmptyMeshCount} non-empty meshes, " +
            $"{accessors.Count} accessors, {bufferViews.Count} bufferViews, {binaryData.Length} bytes");

        return (model, binaryData);
    }

    private static int AppendPositionAccessor(
        MeshData mesh,
        List<GltfBufferView> bufferViews,
        List<GltfAccessor> accessors,
        ref int bufferOffset)
    {
        int byteOffset = bufferOffset;
        int byteLength = mesh.Positions.Length * sizeof(float);
        var (min, max) = ComputeBounds(mesh.Positions);

        int bufferViewIndex = bufferViews.Count;
        bufferViews.Add(new GltfBufferView(0, byteOffset, byteLength, GltfConstants.TargetArrayBuffer));

        int accessorIndex = accessors.Count;
        accessors.Add(new GltfAccessor(
            bufferViewIndex,
            0,
            GltfConstants.ComponentTypeFloat,
            mesh.VertexCount,
            GltfConstants.TypeVec3,
            min,
            max));

        bufferOffset += byteLength;
        return accessorIndex;
    }

    private static int? AppendNormalAccessor(
        MeshData mesh,
        List<GltfBufferView> bufferViews,
        List<GltfAccessor> accessors,
        ref int bufferOffset)
    {
        if (mesh.Normals is null || mesh.Normals.Length == 0)
            return null;

        if (mesh.Normals.Length != mesh.Positions.Length)
        {
            SmartConLogger.Warn(
                $"Mesh '{mesh.NodeName}' normals length ({mesh.Normals.Length}) does not match positions length ({mesh.Positions.Length}). " +
                "Normals skipped. [Action: verify Revit extractor output; positions-only preview will still render]");
            return null;
        }

        int byteOffset = bufferOffset;
        int byteLength = mesh.Normals.Length * sizeof(float);

        int bufferViewIndex = bufferViews.Count;
        bufferViews.Add(new GltfBufferView(0, byteOffset, byteLength, GltfConstants.TargetArrayBuffer));

        int accessorIndex = accessors.Count;
        accessors.Add(new GltfAccessor(
            bufferViewIndex,
            0,
            GltfConstants.ComponentTypeFloat,
            mesh.VertexCount,
            GltfConstants.TypeVec3,
            null,
            null));

        bufferOffset += byteLength;
        return accessorIndex;
    }

    private static int AppendIndexAccessor(
        MeshData mesh,
        List<GltfBufferView> bufferViews,
        List<GltfAccessor> accessors,
        ref int bufferOffset)
    {
        int indexComponentType = mesh.VertexCount <= ushort.MaxValue
            ? GltfConstants.ComponentTypeUnsignedShort
            : GltfConstants.ComponentTypeUnsignedInt;
        int indexSize = indexComponentType == GltfConstants.ComponentTypeUnsignedShort
            ? sizeof(ushort)
            : sizeof(uint);

        int byteOffset = bufferOffset;
        int byteLength = mesh.Indices.Length * indexSize;

        int bufferViewIndex = bufferViews.Count;
        bufferViews.Add(new GltfBufferView(0, byteOffset, byteLength, GltfConstants.TargetElementArrayBuffer));

        int accessorIndex = accessors.Count;
        accessors.Add(new GltfAccessor(
            bufferViewIndex,
            0,
            indexComponentType,
            mesh.Indices.Length,
            GltfConstants.TypeScalar,
            null,
            null));

        bufferOffset += byteLength;
        return accessorIndex;
    }

    private static int AppendMaterial(MeshData mesh, List<GltfMaterial> materials)
    {
        var pbr = new GltfPbrMetallicRoughness(
            new[] { mesh.DiffuseColor.X, mesh.DiffuseColor.Y, mesh.DiffuseColor.Z, mesh.DiffuseColor.W },
            0.0f,
            0.5f);
        var material = new GltfMaterial($"mat_{mesh.NodeName}", pbr);
        materials.Add(material);
        return materials.Count - 1;
    }

    private static byte[] BuildBinaryData(FamilyGeometryPreview preview, int totalLength)
    {
        var buffer = new byte[totalLength];
        int offset = 0;

        foreach (var mesh in preview.Meshes)
        {
            if (mesh.IsEmpty)
                continue;

            int positionByteLength = mesh.Positions.Length * sizeof(float);
            System.Buffer.BlockCopy(mesh.Positions, 0, buffer, offset, positionByteLength);
            offset += positionByteLength;

            if (mesh.Normals is not null && mesh.Normals.Length == mesh.Positions.Length)
            {
                int normalByteLength = mesh.Normals.Length * sizeof(float);
                System.Buffer.BlockCopy(mesh.Normals, 0, buffer, offset, normalByteLength);
                offset += normalByteLength;
            }

            WriteIndices(mesh, buffer, ref offset);
            offset = Align(offset, 4);
        }

        return buffer;
    }

    private static int Align(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static void WriteIndices(MeshData mesh, byte[] buffer, ref int offset)
    {
        bool useShort = mesh.VertexCount <= ushort.MaxValue;
        int indexCount = mesh.Indices.Length;
        if (useShort)
        {
            var ushortIndices = new ushort[indexCount];
            for (int i = 0; i < indexCount; i++)
                ushortIndices[i] = (ushort)mesh.Indices[i];
            int byteLength = indexCount * sizeof(ushort);
            System.Buffer.BlockCopy(ushortIndices, 0, buffer, offset, byteLength);
            offset += byteLength;
        }
        else
        {
            var uintIndices = new uint[indexCount];
            for (int i = 0; i < indexCount; i++)
                uintIndices[i] = (uint)mesh.Indices[i];
            int byteLength = indexCount * sizeof(uint);
            System.Buffer.BlockCopy(uintIndices, 0, buffer, offset, byteLength);
            offset += byteLength;
        }
    }

    private static float[] GetRootTransformMatrix()
    {
        // Revit coordinate system: Z-up (X right, Y forward, Z up), right-handed.
        // glTF 2.0 coordinate system: Y-up (X right, Y up, Z toward viewer), right-handed.
        // A -90° rotation around X maps Revit (x, y, z) to glTF (x, z, -y),
        // sending Revit +Z up to glTF +Y up.
        //
        // System.Numerics.Matrix4x4 is row-major and uses row-vector convention (v * M).
        // glTF stores matrices in column-major order and uses column-vector convention (M * v).
        // To represent the same physical rotation in glTF, we store the transpose of the
        // .NET matrix; otherwise the model is flipped upside down (Y becomes -Y).
        var netMatrix = Matrix4x4.CreateRotationX(-(float)Math.PI / 2f);
        var gltfMatrix = Matrix4x4.Transpose(netMatrix);
        var m = gltfMatrix;
        return new[]
        {
            m.M11, m.M21, m.M31, m.M41,
            m.M12, m.M22, m.M32, m.M42,
            m.M13, m.M23, m.M33, m.M43,
            m.M14, m.M24, m.M34, m.M44
        };
    }

    private static (float[] Min, float[] Max) ComputeBounds(float[] positions)
    {
        if (positions.Length < 3)
            return (new[] { 0.0f, 0.0f, 0.0f }, new[] { 0.0f, 0.0f, 0.0f });

        float minX = positions[0];
        float minY = positions[1];
        float minZ = positions[2];
        float maxX = minX;
        float maxY = minY;
        float maxZ = minZ;

        for (int i = 3; i < positions.Length; i += 3)
        {
            float x = positions[i];
            float y = positions[i + 1];
            float z = positions[i + 2];

            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }

        return (new[] { minX, minY, minZ }, new[] { maxX, maxY, maxZ });
    }
}
