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
    private bool _isLazy3DExtractionRunning;
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
    /// The setter only changes the backing field and raises PropertyChanged;
    /// preview loading is triggered explicitly by <see cref="ChangeSelected3DTypeCommand"/>
    /// or by the 3D viewport Loaded handler.</summary>
    public string? Selected3DTypeName
    {
        get => _selected3DTypeName;
        set => SetProperty(ref _selected3DTypeName, value);
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
    /// assets and preserves the current selection when possible. Called from
    /// <see cref="LoadAssetsAsync"/> after assets are loaded.
    /// </summary>
    public void Populate3DTypeNames()
    {
        var previousSelection = _selected3DTypeName;
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

        if (Available3DTypeNames.Count > 0)
        {
            _selected3DTypeName = previousSelection is not null && Available3DTypeNames.Contains(previousSelection)
                ? previousSelection
                : Available3DTypeNames[0];
            OnPropertyChanged(nameof(Selected3DTypeName));
        }
        else if (_selected3DTypeName is not null)
        {
            _selected3DTypeName = null;
            OnPropertyChanged(nameof(Selected3DTypeName));
        }

        SmartConLogger.Info(
            $"Populate3DTypeNames: found {Available3DTypeNames.Count} type(s) " +
            $"from auto-extracted GLB assets (VersionLabel='{VersionLabel ?? "<null>"}', " +
            $"Model3DAssets.Count={Model3DAssets.Count}), " +
            $"selected3DTypeName='{_selected3DTypeName ?? "<null>"}'");
    }

    [RelayCommand]
    private async Task ChangeSelected3DType(string? typeName)
    {
        // ComboBox fires SelectionChanged while its ItemsSource is being
        // refreshed (clear + repopulate). These null events are expected and
        // harmless; do not log them to avoid noise.
        if (typeName is null) return;

        using var _scope = SmartConLogger.BeginScope("FMProperties3D",
            ("Method", nameof(ChangeSelected3DType)),
            ("TypeName", typeName));

        if (Selected3DTypeName == typeName)
        {
            SmartConLogger.Info("ChangeSelected3DType: ignored same typeName");
            return;
        }

        Selected3DTypeName = typeName;
        SmartConLogger.Info("ChangeSelected3DType: selection changed, loading preview");
        await Load3DPreviewForTypeAsync(Selected3DTypeName, System.Threading.CancellationToken.None).ConfigureAwait(true);
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

    void System.IDisposable.Dispose()
    {
        Tags.CollectionChanged -= OnTagsCollectionChanged;
        Dispose3DResources();
    }
}
