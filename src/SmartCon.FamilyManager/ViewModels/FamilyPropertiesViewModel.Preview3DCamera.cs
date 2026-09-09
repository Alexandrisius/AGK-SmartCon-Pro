using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Geometry;
using SmartCon.UI;
using Media3D = System.Windows.Media.Media3D;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
    /// <summary>
    /// Restores the camera position, look direction and up direction that were
    /// saved before a type swap. If the saved state is invalid or the camera
    /// object is missing, falls back to <see cref="FitCameraToScene"/>.
    /// </summary>
    private void RestoreCameraState()
    {
        if (Camera3D is null) return;

        try
        {
            Camera3D.Position = _savedCameraPosition;
            Camera3D.LookDirection = _savedCameraLookDirection;
            Camera3D.UpDirection = _savedCameraUpDirection;
            SmartConLogger.Debug("Camera state restored after type swap");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"RestoreCameraState failed: {ex.Message} [Action: falling back to FitCameraToScene]");
            FitCameraToScene();
        }
    }

    /// <summary>
    /// Walk the HelixToolkit scene graph recursively. If any
    /// <see cref="MeshNode"/> has <see cref="MeshNode.Material"/> = null,
    /// assign a default <see cref="PhongMaterialCore"/> with a neutral gray
    /// diffuse color so the mesh is actually rendered (HelixToolkit renders
    /// null-material meshes as invisible — i.e. nothing appears).
    /// </summary>
    private static void EnsureMaterialsRecursive(SceneNode node, ref int nullCount, ref int totalCount)
    {
        if (node is MeshNode mesh)
        {
            totalCount++;
            if (mesh.Material is null)
            {
                mesh.Material = new PhongMaterialCore
                {
                    DiffuseColor = new Color4(0.7f, 0.7f, 0.7f, 1.0f),
                    AmbientColor = new Color4(0.3f, 0.3f, 0.3f, 1.0f),
                    SpecularColor = new Color4(0.2f, 0.2f, 0.2f, 1.0f),
                    SpecularShininess = 32.0f
                };
                nullCount++;
            }
            else
            {
                if (mesh.Material is PhongMaterialCore phong)
                {
                    var d = phong.DiffuseColor;
                    var a = phong.AmbientColor;

                    var fixedColor = false;
                    if (d.Red < 0.01f && d.Green < 0.01f && d.Blue < 0.01f)
                    {
                        SmartConLogger.Warn(
                            $"EnsureMaterials: mesh '{mesh.Name}' has BLACK DiffuseColor — forcing to white " +
                            "[Action: Assimp imported material with zero diffuse, see helix-toolkit #2421]");
                        phong.DiffuseColor = new Color4(0.8f, 0.8f, 0.8f, 1.0f);
                        fixedColor = true;
                    }
                    if (a.Red < 0.01f && a.Green < 0.01f && a.Blue < 0.01f)
                    {
                        SmartConLogger.Warn(
                            $"EnsureMaterials: mesh '{mesh.Name}' has BLACK AmbientColor — forcing to gray " +
                            "[Action: without ambient light the lit faces facing away from the light source render black]");
                        phong.AmbientColor = new Color4(0.3f, 0.3f, 0.3f, 1.0f);
                        fixedColor = true;
                    }
                    if (fixedColor)
                    {
                        phong.SpecularColor = new Color4(0.2f, 0.2f, 0.2f, 1.0f);
                        phong.SpecularShininess = 32.0f;
                    }
                }
                else if (mesh.Material is PBRMaterialCore pbr)
                {
                    var a = pbr.AlbedoColor;

                    if (a.Red < 0.01f && a.Green < 0.01f && a.Blue < 0.01f)
                    {
                        SmartConLogger.Warn(
                            $"EnsureMaterials: mesh '{mesh.Name}' has BLACK AlbedoColor — forcing to white " +
                            "[Action: Assimp imported material with zero albedo, see helix-toolkit #2421]");
                        pbr.AlbedoColor = new Color4(0.8f, 0.8f, 0.8f, 1.0f);
                    }
                }
            }

            if (mesh.Geometry is null)
            {
                SmartConLogger.Warn(
                    $"EnsureMaterials: mesh '{mesh.Name ?? "<unnamed>"}' has null Geometry " +
                    "[Action: this mesh won't be rendered]");
            }
        }

        foreach (var child in node.Items)
        {
            EnsureMaterialsRecursive(child, ref nullCount, ref totalCount);
        }
    }

    /// <summary>
    /// Compute the bounding sphere of all meshes in <see cref="Scene3DRoot"/>
    /// and reposition <see cref="Camera3D"/> so the model fits the viewport.
    /// Called after <see cref="Scene3DRoot.AddNode"/> in
    /// <see cref="Load3DPreviewForTypeAsync"/> and from <see cref="ResetCamera3DCommand"/>.
    /// </summary>
    private void FitCameraToScene()
    {
        if (Camera3D is null) return;

        try
        {
            // Walk scene graph and accumulate bounding box corners.
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var hasBounds = false;

            void Walk(SceneNode node)
            {
                // SceneNode.Bounds is a BoundingBox; use reflection-free API
                // via the BoundsWithTransform property (BoundingSphere) when
                // available, else fall back to walking MeshNode.Geometry.
                if (node is MeshNode mesh && mesh.Geometry is not null)
                {
                    var positions = mesh.Geometry.Positions;
                    if (positions is not null && positions.Count > 0)
                    {
                        foreach (var p in positions)
                        {
                            if (p.X < minX) minX = p.X;
                            if (p.Y < minY) minY = p.Y;
                            if (p.Z < minZ) minZ = p.Z;
                            if (p.X > maxX) maxX = p.X;
                            if (p.Y > maxY) maxY = p.Y;
                            if (p.Z > maxZ) maxZ = p.Z;
                            hasBounds = true;
                        }
                    }
                }
                foreach (var child in node.Items)
                {
                    Walk(child);
                }
            }

            foreach (var node in Scene3DRoot.SceneNode.Items)
            {
                Walk(node);
            }

            if (!hasBounds)
            {
                SmartConLogger.Warn(
                    "FitCameraToScene: no mesh positions found — camera stays at default " +
                    "[Action: click «Показать всё» button once viewport is visible to fit manually]");
                return;
            }

            var center = new Media3D.Point3D(
                (minX + maxX) / 2.0,
                (minY + maxY) / 2.0,
                (minZ + maxZ) / 2.0);

            var sizeX = maxX - minX;
            var sizeY = maxY - minY;
            var sizeZ = maxZ - minZ;
            var maxDim = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
            if (maxDim < 0.0001) maxDim = 1.0;

            // FOV-aware distance calculation. PerspectiveCamera default FOV=45°.
            // For a bounding box, the camera must be far enough so the entire
            // box fits within the FOV. The diagonal of the bounding box is the
            // worst-case dimension. Use: distance = diag / (2 * tan(FOV/2)) * margin.
            var diag = Math.Sqrt(sizeX * sizeX + sizeY * sizeY + sizeZ * sizeZ);
            if (diag < 0.0001) diag = maxDim;
            var fov = Camera3D.FieldOfView > 0 ? Camera3D.FieldOfView : 45.0;
            var fovRad = fov * Math.PI / 180.0;
            var distance = (diag / (2.0 * Math.Tan(fovRad / 2.0))) * 2.5;
            var dir = new Media3D.Vector3D(1, 1, 1);
            dir.Normalize();

            Camera3D.Position = new Media3D.Point3D(
                center.X + dir.X * distance,
                center.Y + dir.Y * distance,
                center.Z + dir.Z * distance);
            Camera3D.LookDirection = new Media3D.Vector3D(
                center.X - Camera3D.Position.X,
                center.Y - Camera3D.Position.Y,
                center.Z - Camera3D.Position.Z);
            Camera3D.UpDirection = new Media3D.Vector3D(0, 1, 0);
            Camera3D.NearPlaneDistance = Math.Max(0.001, maxDim * 0.001);
            Camera3D.FarPlaneDistance = maxDim * 1000.0;

            SmartConLogger.Info(
                $"Camera fit to model: center=({center.X.ToString("F2")},{center.Y.ToString("F2")},{center.Z.ToString("F2")}) " +
                $"maxDim={maxDim.ToString("F2")} diag={diag.ToString("F2")} fov={fov.ToString("F1")}° distance={distance.ToString("F2")}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"FitCameraToScene failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: нажмите «Показать всё» (ZoomExtents) на тулбаре вьювера]");
        }
    }

    /// <summary>
    /// Reset the camera back to its default position/orientation.
    /// Wires to the toolbar "Reset view" button.
    /// </summary>
    [RelayCommand]
    private void ResetCamera3D()
    {
        if (Camera3D is null) return;
        // Re-fit to current scene bounds (same as initial fit).
        FitCameraToScene();
    }

    /// <summary>
    /// Toggle wireframe render mode on all meshes in <see cref="Scene3DRoot"/>.
    /// Iterates the scene graph (using <see cref="SceneNode.Items"/>) and flips
    /// <see cref="MeshNode.RenderWireframe"/> per node.
    /// </summary>
    [RelayCommand]
    private void ToggleWireframe3D()
    {
        if (EffectsManager3D is null) return;
        var newValue = !IsWireframeVisible;
        try
        {
            foreach (var node in Scene3DRoot.SceneNode.Items)
            {
                ToggleWireframeRecursive(node, newValue);
            }
            IsWireframeVisible = newValue;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"ToggleWireframe3D failed: {ex.Message} [Action: wireframe render mode unchanged]");
        }
    }

    private static void ToggleWireframeRecursive(SceneNode node, bool wireframe)
    {
        if (node is MeshNode meshNode)
        {
            meshNode.RenderWireframe = wireframe;
        }
        foreach (var child in node.Items)
        {
            ToggleWireframeRecursive(child, wireframe);
        }
    }
}
