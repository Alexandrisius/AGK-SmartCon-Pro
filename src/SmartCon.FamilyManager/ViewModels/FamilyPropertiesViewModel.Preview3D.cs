using System.Collections.Generic;
using System.Collections.ObjectModel;
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

/// <summary>
/// 3D preview partial for <see cref="FamilyPropertiesViewModel"/>.
/// </summary>
/// <remarks>
/// <para><b>ADR-042:</b> HelixToolkit.Wpf.SharpDX DirectX 11 viewer bound to
/// a GLB auto-extracted at family import time. The GLB is loaded via
/// <see cref="GlbSceneLoader"/> (which wraps
/// <c>HelixToolkit.Wpf.SharpDX.Assimp.Importer</c>) and the resulting
/// <see cref="SceneNode"/> is added to <see cref="Scene3DRoot"/>.</para>
/// <para><b>Multi-version:</b> HelixToolkit.Wpf.SharpDX 3.1.2 supports both
/// net8.0-windows (R2025+) and net48 (R2019-R2024). Workaround for HelixToolkit's
/// conflict with PolySharp's source-generated DefaultInterpolatedStringHandler
/// (which breaks $"...{x:F0}..." overload resolution): all format-specifier
/// interpolations have been refactored to explicit <c>.ToString("F0")</c> calls
/// so compilation succeeds on net48 when HelixToolkit transitively pulls
/// System.Runtime 4.3.0 via SharpDX. This means full 3D preview is now
/// available on both R2025+ (net8) and R2019-R2024 (net48).</para>
/// <para><b>Lifecycle:</b> <see cref="Initialize3DInfrastructure"/> must be
/// called once after construction (lazy DirectX init). <see cref="Load3DPreviewForTypeAsync"/>
/// is called from <c>InitializeAsync</c>/<c>LoadAssetsAsync</c> after assets are
/// fetched. <see cref="Dispose3DResources"/> is called from the View's
/// <c>Closed</c> event.</para>
/// </remarks>
public sealed partial class FamilyPropertiesViewModel
{
    private const string AutoExtractedPreviewPrefix = "auto-extracted-preview:";

    private bool _isFirst3DLoad = true;
    private Media3D.Point3D _savedCameraPosition;
    private Media3D.Vector3D _savedCameraLookDirection;
    private Media3D.Vector3D _savedCameraUpDirection;

    private IEffectsManager? _effectsManager3D;
    /// <summary>DirectX 11 resource manager (1:1 with the viewport).
    /// Must raise PropertyChanged so XAML binding updates after init.</summary>
    public IEffectsManager? EffectsManager3D
    {
        get => _effectsManager3D;
        private set => SetProperty(ref _effectsManager3D, value);
    }

    private PerspectiveCamera? _camera3D;
    /// <summary>Perspective camera bound to <c>Viewport3DX.Camera</c>.
    /// Must raise PropertyChanged so XAML binding updates after init.</summary>
    public PerspectiveCamera? Camera3D
    {
        get => _camera3D;
        private set => SetProperty(ref _camera3D, value);
    }

    /// <summary>Group container for dynamically loaded scene nodes.
    /// Bound via <c>&lt;hx:Element3DPresenter Content="{Binding Scene3DRoot}"/&gt;</c>
    /// in XAML — direct child of Viewport3DX (FileLoadDemo pattern).</summary>
    public SceneNodeGroupModel3D Scene3DRoot { get; } = new();

    [ObservableProperty] private bool _isLoading3D;
    [ObservableProperty] private bool _has3DPreview;
    [ObservableProperty] private string? _preview3DStatusMessage;
    [ObservableProperty] private bool _isWireframeVisible;

    /// <summary>Type names extracted from auto-extracted GLB assets
    /// (one per family type). Populated in <see cref="LoadAssetsAsync"/>.
    /// Empty collection hides the ComboBox.</summary>
    public ObservableCollection<string> Available3DTypeNames { get; } = new();

    private string? _selected3DTypeName;
    /// <summary>Currently selected type name from <see cref="Available3DTypeNames"/>.
    /// When changed, the GLB for this type is loaded into the viewport.</summary>
    public string? Selected3DTypeName
    {
        get => _selected3DTypeName;
        set
        {
            if (SetProperty(ref _selected3DTypeName, value))
            {
                _ = Load3DPreviewForTypeAsync(value, System.Threading.CancellationToken.None);
            }
        }
    }

    /// <summary>True when <see cref="Available3DTypeNames"/> has more than
    /// one entry — drives the ComboBox visibility.</summary>
    public bool HasMultiple3DTypes => Available3DTypeNames.Count > 1;

    /// <summary>Inverse of <see cref="Has3DPreview"/>/<see cref="IsLoading3D"/> —
    /// drives the "no preview available" overlay.</summary>
    public bool HasNo3DPreview => !Has3DPreview && !IsLoading3D;

    partial void OnHas3DPreviewChanged(bool value) => OnPropertyChanged(nameof(HasNo3DPreview));
    partial void OnIsLoading3DChanged(bool value) => OnPropertyChanged(nameof(HasNo3DPreview));

    /// <summary>
    /// Lazily initializes the DirectX effects manager, camera, and lights.
    /// Idempotent — safe to call multiple times. Called from the View's
    /// <see cref="System.Windows.FrameworkElement.Loaded"/> event via
    /// <c>FamilyPropertiesView.OnLoaded</c> so the DirectX device is only
    /// created after the window is on screen (DirectX requires a visible HWND).
    /// </summary>
    public void Initialize3DInfrastructure()
    {
        if (EffectsManager3D is not null) return;

        using var _scope = SmartConLogger.BeginScope("FMProperties3D",
            ("Method", nameof(Initialize3DInfrastructure)));
        try
        {
            EffectsManager3D = new DefaultEffectsManager();
            Camera3D = new PerspectiveCamera
            {
                Position = new Media3D.Point3D(3, 3, 5),
                LookDirection = new Media3D.Vector3D(-3, -3, -5),
                UpDirection = new Media3D.Vector3D(0, 1, 0),
                FarPlaneDistance = 5000,
                NearPlaneDistance = 0.1
            };

            // Lights are defined in XAML as direct children of Viewport3DX
            // (FileLoadDemo pattern). Scene3DRoot is bound via Element3DPresenter.

            SmartConLogger.Info("3D viewer infrastructure initialized");
            // GLB loading is triggered from View.OnViewport3DXLoaded after
            // Scene3DRoot is added to Viewport3DX.Items.
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to initialize 3D viewer infrastructure: {ex.GetType().Name}: {ex.Message} " +
                "[Action: 3D preview tab will show an error message; other tabs are unaffected. " +
                "Verify DirectX 11 is available on this system]");
            Preview3DStatusMessage = LanguageManager.GetString(
                StringLocalization.Keys.FM_3D_InitFailed) ?? "Failed to initialize 3D viewer";
        }
    }

    /// <summary>
    /// Populates <see cref="Available3DTypeNames"/> from auto-extracted GLB
    /// assets and selects the first type. Called from <see cref="LoadAssetsAsync"/>
    /// after assets are loaded.
    /// </summary>
    public void Populate3DTypeNames()
    {
        Available3DTypeNames.Clear();

        foreach (var asset in Model3DAssets)
        {
            if (string.IsNullOrEmpty(asset.Description)) continue;
            if (!asset.Description!.StartsWith(AutoExtractedPreviewPrefix, System.StringComparison.Ordinal)) continue;
            if (!string.Equals(asset.VersionLabel, VersionLabel, System.StringComparison.Ordinal)) continue;

            var suffix = asset.Description![AutoExtractedPreviewPrefix.Length..];
            var sepIdx = suffix.IndexOf("::", System.StringComparison.Ordinal);
            var typeName = sepIdx >= 0 && sepIdx + 2 < suffix.Length
                ? suffix[(sepIdx + 2)..]
                : "";

            if (!string.IsNullOrEmpty(typeName) && !Available3DTypeNames.Contains(typeName))
            {
                Available3DTypeNames.Add(typeName);
            }
        }

        OnPropertyChanged(nameof(HasMultiple3DTypes));

        if (Available3DTypeNames.Count > 0 && _selected3DTypeName is null)
        {
            _selected3DTypeName = Available3DTypeNames[0];
            OnPropertyChanged(nameof(Selected3DTypeName));
        }
    }

    /// <summary>
    /// Find the auto-extracted GLB asset for the given type, resolve its path,
    /// load it via <see cref="GlbSceneLoader"/>, and add it to
    /// <see cref="Scene3DRoot"/>. Clears any previously-loaded scene first.
    /// </summary>
    /// <param name="typeName">Type name from <see cref="Available3DTypeNames"/>,
    /// or <c>null</c> to load the default (no-type) GLB.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task Load3DPreviewForTypeAsync(string? typeName, CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FMProperties3D",
            ("Method", nameof(Load3DPreviewForTypeAsync)),
            ("TypeName", typeName ?? "<null>"));

        // Critical: HelixToolkit requires scene.Root.Attach(effectsManager)
        // BEFORE AddNode — otherwise mesh nodes never get GPU vertex buffers
        // allocated and the viewport renders black (issue helix-toolkit #2215).
        // If the viewer hasn't been initialized yet (View.Loaded hasn't fired),
        // defer the actual load until Initialize3DInfrastructure completes.
        if (EffectsManager3D is null)
        {
            SmartConLogger.Info(
                "Load3DPreviewForTypeAsync: EffectsManager3D is null — deferring load " +
                "until Initialize3DInfrastructure is called from View.Loaded");
            return;
        }

        try
        {
            IsLoading3D = true;
            Preview3DStatusMessage = null;

            // Preserve the user's camera when only switching types. The first
            // load (or any load after a version/family change) should fit the
            // camera to the new scene; subsequent type swaps keep the camera.
            var preserveCamera = false;
            if (!_isFirst3DLoad && Camera3D is { } camera)
            {
                preserveCamera = true;
                _savedCameraPosition = camera.Position;
                _savedCameraLookDirection = camera.LookDirection;
                _savedCameraUpDirection = camera.UpDirection;
                SmartConLogger.Debug("Load3DPreviewForTypeAsync: preserving user camera for type swap");
            }

            Scene3DRoot.Clear();

            var typeSuffix = typeName is null ? "" : typeName;
            var descriptionSuffix = "::" + typeSuffix;

            var autoAsset = Model3DAssets.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.Description)
                && a.Description!.EndsWith(descriptionSuffix, System.StringComparison.Ordinal)
                && a.Description.StartsWith(AutoExtractedPreviewPrefix, System.StringComparison.Ordinal)
                && string.Equals(a.VersionLabel, VersionLabel, System.StringComparison.Ordinal));

            if (autoAsset is null)
            {
                Has3DPreview = false;
                Preview3DStatusMessage = LanguageManager.GetString(
                    StringLocalization.Keys.FM_3D_NoPreview) ?? "No 3D preview for this version";
                SmartConLogger.Info($"No auto-extracted GLB asset found for type='{typeName}' — preview will show placeholder");
                return;
            }

            var glbPath = await _assetService.ResolveAssetPathAsync(autoAsset.Id, ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(glbPath))
            {
                Has3DPreview = false;
                Preview3DStatusMessage = LanguageManager.GetString(
                    StringLocalization.Keys.FM_3D_NoPreview) ?? "No 3D preview for this version";
                SmartConLogger.Warn(
                    $"GLB asset '{autoAsset.FileName}' resolved to null path " +
                    "[Action: re-import the family to regenerate the GLB, or check managed storage]");
                return;
            }

            // Offload I/O + Assimp parse to ThreadPool (keeps UI responsive
            // for large meshes; Assimp is C++/P-Invoke so ThreadPool-safe).
            var scene = await Task.Run(
                () => GlbSceneLoader.LoadScene(glbPath!),
                ct).ConfigureAwait(true);

            if (scene is null)
            {
                Has3DPreview = false;
                Preview3DStatusMessage = LanguageManager.GetString(
                    StringLocalization.Keys.FM_3D_LoadFailed) ?? "Failed to load 3D preview";
                return;
            }

            // scene.Attach creates GPU vertex buffers for each node.
            scene.Attach(EffectsManager3D);
            SmartConLogger.Info("scene.Attach(EffectsManager3D) done");

            Scene3DRoot.AddNode(scene);
            SmartConLogger.Info("3D preview loaded and added to Scene3DRoot");

            // Walk the loaded scene: ensure every MeshNode has a non-null
            // MaterialCore. Assimp importer sometimes creates meshes without
            // materials, which renders as invisible.
            int nullMaterialCount = 0;
            int totalMeshCount = 0;
            EnsureMaterialsRecursive(scene, ref nullMaterialCount, ref totalMeshCount);
            SmartConLogger.Info(
                $"EnsureMaterials: {totalMeshCount} meshes scanned, " +
                $"{nullMaterialCount} had null Material and received default PhongMaterialCore");

            // Compute world-space transforms after AddNode so the render host
            // has correct matrices for the vertex shader (issue #2215, #2013).
            scene.UpdateAllTransformMatrix();
            SmartConLogger.Info("scene.UpdateAllTransformMatrix() done");

            if (_isFirst3DLoad)
            {
                FitCameraToScene();
                _isFirst3DLoad = false;
            }
            else if (preserveCamera)
            {
                RestoreCameraState();
            }

            Has3DPreview = true;
        }
        catch (OperationCanceledException)
        {
            Has3DPreview = false;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Load3DPreviewForTypeAsync failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: 3D preview will show error overlay; check smartcon.log for details; " +
                "re-import the family if the GLB file is corrupted]");
            Has3DPreview = false;
            Preview3DStatusMessage = LanguageManager.GetString(
                StringLocalization.Keys.FM_3D_LoadFailed) ?? "Failed to load 3D preview";
        }
        finally
        {
            IsLoading3D = false;
        }
    }

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

    /// <summary>
    /// Dispose DirectX resources (EffectsManager + camera). Idempotent.
    /// Called from <c>FamilyPropertiesView.Closed</c> event via
    /// <c>((IDisposable)DataContext).Dispose()</c>.
    /// </summary>
    public void Dispose3DResources()
    {
        using var _scope = SmartConLogger.BeginScope("FMProperties3D",
            ("Method", nameof(Dispose3DResources)));
        try
        {
            Scene3DRoot.Clear();
            EffectsManager3D?.Dispose();
            EffectsManager3D = null;
            Camera3D = null;
            SmartConLogger.Info("3D viewer resources disposed");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Error during 3D viewer dispose: {ex.Message} [Action: не критично — DirectX resources будут освобождены ОС при выходе процесса]");
        }
    }

    void System.IDisposable.Dispose() => Dispose3DResources();
}
