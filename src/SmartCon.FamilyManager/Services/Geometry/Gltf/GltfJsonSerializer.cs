using System.Globalization;
using System.Text;

namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal static class GltfJsonSerializer
{
    public static string Serialize(GltfModel model)
    {
        var sb = new StringBuilder(4096);
        sb.Append('{');

        WriteAsset(sb, model.Asset);
        sb.Append(",\"scene\":0");
        WriteScenes(sb, model.Scenes);
        WriteNodes(sb, model.Nodes);
        WriteMeshes(sb, model.Meshes);
        WriteMaterials(sb, model.Materials);
        WriteAccessors(sb, model.Accessors);
        WriteBufferViews(sb, model.BufferViews);
        WriteBuffers(sb, model.Buffers);

        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteAsset(StringBuilder sb, GltfAsset asset)
    {
        sb.Append("\"asset\":{\"version\":");
        WriteString(sb, asset.Version);
        if (!string.IsNullOrEmpty(asset.Generator))
        {
            sb.Append(",\"generator\":");
            WriteString(sb, asset.Generator);
        }
        sb.Append('}');
    }

    private static void WriteScenes(StringBuilder sb, IReadOnlyList<GltfScene> scenes)
    {
        if (scenes.Count == 0) return;
        sb.Append(",\"scenes\":[");
        for (int i = 0; i < scenes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"nodes\":[");
            WriteIntArray(sb, scenes[i].Nodes);
            sb.Append("]}");
        }
        sb.Append(']');
    }

    private static void WriteNodes(StringBuilder sb, IReadOnlyList<GltfNode> nodes)
    {
        if (nodes.Count == 0) return;
        sb.Append(",\"nodes\":[");
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var node = nodes[i];
            sb.Append('{');
            bool hasPrevious = false;
            if (!string.IsNullOrEmpty(node.Name))
            {
                sb.Append("\"name\":");
                WriteString(sb, node.Name);
                hasPrevious = true;
            }
            if (node.Mesh.HasValue)
            {
                if (hasPrevious) sb.Append(',');
                sb.Append("\"mesh\":");
                sb.Append(node.Mesh.Value.ToString(CultureInfo.InvariantCulture));
                hasPrevious = true;
            }
            if (node.Children is not null && node.Children.Count > 0)
            {
                if (hasPrevious) sb.Append(',');
                sb.Append("\"children\":[");
                WriteIntArray(sb, node.Children);
                sb.Append(']');
                hasPrevious = true;
            }
            if (node.Matrix is not null)
            {
                if (hasPrevious) sb.Append(',');
                sb.Append("\"matrix\":[");
                WriteFloatArray(sb, node.Matrix);
                sb.Append(']');
            }
            sb.Append('}');
        }
        sb.Append(']');
    }

    private static void WriteMeshes(StringBuilder sb, IReadOnlyList<GltfMesh> meshes)
    {
        if (meshes.Count == 0) return;
        sb.Append(",\"meshes\":[");
        for (int i = 0; i < meshes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var mesh = meshes[i];
            sb.Append("{\"name\":");
            WriteString(sb, mesh.Name);
            sb.Append(",\"primitives\":[");
            for (int j = 0; j < mesh.Primitives.Count; j++)
            {
                if (j > 0) sb.Append(',');
                var prim = mesh.Primitives[j];
                sb.Append("{\"attributes\":{\"POSITION\":");
                sb.Append(prim.Attributes.Position.ToString(CultureInfo.InvariantCulture));
                if (prim.Attributes.Normal.HasValue)
                {
                    sb.Append(",\"NORMAL\":");
                    sb.Append(prim.Attributes.Normal.Value.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append("},\"indices\":");
                sb.Append(prim.Indices.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"material\":");
                sb.Append(prim.Material.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"mode\":");
                sb.Append(prim.Mode.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append(']');
    }

    private static void WriteMaterials(StringBuilder sb, IReadOnlyList<GltfMaterial> materials)
    {
        if (materials.Count == 0) return;
        sb.Append(",\"materials\":[");
        for (int i = 0; i < materials.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var mat = materials[i];
            sb.Append("{\"name\":");
            WriteString(sb, mat.Name);
            sb.Append(",\"pbrMetallicRoughness\":{\"baseColorFactor\":[");
            WriteFloatArray(sb, mat.PbrMetallicRoughness.BaseColorFactor);
            sb.Append("],\"metallicFactor\":");
            sb.Append(FloatToString(mat.PbrMetallicRoughness.MetallicFactor));
            sb.Append(",\"roughnessFactor\":");
            sb.Append(FloatToString(mat.PbrMetallicRoughness.RoughnessFactor));
            sb.Append("}}");
        }
        sb.Append(']');
    }

    private static void WriteAccessors(StringBuilder sb, IReadOnlyList<GltfAccessor> accessors)
    {
        if (accessors.Count == 0) return;
        sb.Append(",\"accessors\":[");
        for (int i = 0; i < accessors.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var a = accessors[i];
            sb.Append("{\"bufferView\":");
            sb.Append(a.BufferView.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"byteOffset\":");
            sb.Append(a.ByteOffset.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"componentType\":");
            sb.Append(a.ComponentType.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"count\":");
            sb.Append(a.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"type\":");
            WriteString(sb, a.Type);
            if (a.Min is not null)
            {
                sb.Append(",\"min\":[");
                WriteFloatArray(sb, a.Min);
                sb.Append(']');
            }
            if (a.Max is not null)
            {
                sb.Append(",\"max\":[");
                WriteFloatArray(sb, a.Max);
                sb.Append(']');
            }
            sb.Append('}');
        }
        sb.Append(']');
    }

    private static void WriteBufferViews(StringBuilder sb, IReadOnlyList<GltfBufferView> bufferViews)
    {
        if (bufferViews.Count == 0) return;
        sb.Append(",\"bufferViews\":[");
        for (int i = 0; i < bufferViews.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var bv = bufferViews[i];
            sb.Append("{\"buffer\":");
            sb.Append(bv.Buffer.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"byteOffset\":");
            sb.Append(bv.ByteOffset.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"byteLength\":");
            sb.Append(bv.ByteLength.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"target\":");
            sb.Append(bv.Target.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        sb.Append(']');
    }

    private static void WriteBuffers(StringBuilder sb, IReadOnlyList<GltfBuffer> buffers)
    {
        if (buffers.Count == 0) return;
        sb.Append(",\"buffers\":[");
        for (int i = 0; i < buffers.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"byteLength\":");
            sb.Append(buffers[i].ByteLength.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        sb.Append(']');
    }

    private static void WriteFloatArray(StringBuilder sb, IReadOnlyList<float> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(FloatToString(values[i]));
        }
    }

    private static void WriteIntArray(StringBuilder sb, IReadOnlyList<int> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string FloatToString(float value) =>
        value.ToString("G9", CultureInfo.InvariantCulture);

    private static void WriteString(StringBuilder sb, string? value)
    {
        if (value is null)
        {
            sb.Append("null");
            return;
        }
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
