#if NET8_0_OR_GREATER
using System.IO;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using SmartCon.Core.Logging;

namespace SmartCon.FamilyManager.Services.Geometry;

/// <summary>
/// Loads a GLB file (binary glTF 2.0) into a HelixToolkit scene graph
/// <see cref="SceneNode"/> suitable for <see cref="SceneNodeGroupModel3D.AddNode"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="HelixToolkit.Wpf.SharpDX.Assimp.Importer"/> — the official
/// production HelixToolkit loader for GLB/glTF/OBJ/FBX. The Assimp importer
/// parses the GLB, builds a <see cref="HelixToolkitScene"/> with a complete
/// scene graph (transforms, materials, animations if any) and returns its
/// <see cref="HelixToolkitScene.Root"/> node ready to be added to a
/// <see cref="SceneNodeGroupModel3D"/>.
/// </para>
/// <para><b>ADR-042:</b> replaces the original custom SharpGLTF→HelixToolkit
/// converter (cross-namespace MeshGeometry3D/Geometry3D mismatch was never
/// resolved cleanly; the Assimp path is the approach used by the official
/// HelixToolkit FileLoadDemo).</para>
/// </remarks>
public static class GlbSceneLoader
{
    /// <summary>
    /// Load a GLB file from disk into a HelixToolkit scene graph root node.
    /// </summary>
    /// <param name="glbPath">Absolute path to the .glb file.</param>
    /// <returns>
    /// The root <see cref="SceneNode"/> of the loaded scene, or <see langword="null"/>
    /// if the file does not exist, the scene is empty, or an error occurred.
    /// </returns>
    public static SceneNode? LoadScene(string glbPath)
    {
        if (!File.Exists(glbPath))
        {
            SmartConLogger.Warn(
                $"GLB load failed: file not found '{Path.GetFileName(glbPath)}' " +
                "[Action: re-import the family to regenerate the 3D preview]");
            return null;
        }

        try
        {
            using var _scope = SmartConLogger.BeginScope("GlbLoad",
                ("Method", nameof(LoadScene)),
                ("FilePath", Path.GetFileName(glbPath)));

            var importer = new Importer();
            var scene = importer.Load(glbPath);

            if (scene is null || scene.Root is null)
            {
                SmartConLogger.Warn(
                    $"GLB parse returned empty scene '{Path.GetFileName(glbPath)}' " +
                    "[Action: 3D preview will be unavailable — re-import the family or check the GLB file]");
                return null;
            }

            // Compute world-space transforms (used downstream for bounding-box
            // calculations during camera FitView). Walk-in-place — safe to skip
            // if no transforms are present (assimp already set them at Load time).

            // Walk the scene graph and count meshes for diagnostic logging.
            int meshCount = 0;
            int triangleCount = 0;
            int totalNodes = 0;
            WalkScene(scene.Root, ref meshCount, ref triangleCount, ref totalNodes);

            SmartConLogger.Info(
                $"GLB scene loaded: {meshCount} meshes, {triangleCount} triangles, {totalNodes} scene nodes");

            return scene.Root;
        }
        catch (Exception ex)
        {
            // Unnest inner exceptions — TypeInitializationException wraps real cause.
            var current = ex;
            int depth = 0;
            while (current is not null && depth < 5)
            {
                SmartConLogger.Warn(
                    $"GLB scene load failed [{depth}]: {current.GetType().Name}: {current.Message} " +
                    "[Action: 3D preview will be unavailable — re-import the family or check the GLB file]");
                current = current.InnerException;
                depth++;
            }
            return null;
        }
    }

    /// <summary>
    /// Recursive depth-first walk of the HelixToolkit scene graph. Counts
    /// meshes and triangles for logging purposes only. Uses
    /// <see cref="SceneNode.Items"/> (the child collection exposed in v3)
    /// instead of <c>Traverse()</c> extension (which lives in a separate
    /// namespace and is unavailable without an extra Extension import).
    /// </summary>
    private static void WalkScene(SceneNode node, ref int meshCount, ref int triangleCount, ref int totalNodes)
    {
        totalNodes++;
        if (node is MeshNode mesh && mesh.Geometry is not null)
        {
            meshCount++;
            var indices = mesh.Geometry.Indices;
            if (indices is not null && indices.Count >= 3)
            {
                triangleCount += indices.Count / 3;
            }
        }

        foreach (var child in node.Items)
        {
            WalkScene(child, ref meshCount, ref triangleCount, ref totalNodes);
        }
    }
}
#else
using System.IO;
using SmartCon.Core.Logging;

namespace SmartCon.FamilyManager.Services.Geometry;

/// <summary>
/// Net48 stub. HelixToolkit.Wpf.SharpDX + HelixToolkit.SharpDX.Assimp are
/// net8.0-windows only (see SmartCon.FamilyManager.csproj and ADR-042).
/// </summary>
public static class GlbSceneLoader
{
    /// <summary>
    /// Net48 stub — always returns <see langword="null"/> with a Warn log.
    /// 3D preview is only available on net8.0-windows (Revit 2025+).
    /// </summary>
    public static object? LoadScene(string glbPath)
    {
        SmartConLogger.Warn(
            $"3D preview viewer is unsupported on net48 (Revit 2019-2024). " +
            $"GLB file '{Path.GetFileName(glbPath)}' exists but cannot be loaded. " +
            "[Action: use Revit 2025+ to view 3D preview]");
        return null;
    }
}
#endif
