using System.Windows;
#if NET8_0_OR_GREATER
using HelixToolkit.Wpf.SharpDX;
#endif
using SmartCon.Core.Logging;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class FamilyPropertiesView : DialogWindowBase
{
    public FamilyPropertiesView(FamilyPropertiesViewModel viewModel)
    {
        SmartConLogger.Info($"FamilyPropertiesView.ctor: start for itemId={viewModel.Name}");
        try
        {
            InitializeComponent();
            SmartConLogger.Info("FamilyPropertiesView.ctor: InitializeComponent OK");
            DataContext = viewModel;
            BindCloseRequest(viewModel);
            InitializeVersionGridHeaders();

            // ADR-042: Viewport3DX is inside a TabItem. WPF TabControl does
            // not layout non-active tabs → ActualWidth=0 → DirectX render host
            // never starts. Instead of Window.Loaded, we use the Viewport3DX's
            // own Loaded event (fires when user switches to the 3D tab).
            // The Window.Loaded handler is kept for early EffectsManager creation.
            Loaded += OnViewModelLoaded;
            Closed += OnViewModelClosed;
            SmartConLogger.Info("FamilyPropertiesView.ctor: done");
        }
        catch (Exception ex)
        {
            // Unnest inner exceptions — XamlParseException wraps real cause.
            var current = ex;
            int depth = 0;
            while (current is not null && depth < 5)
            {
                SmartConLogger.Error(
                    $"FamilyPropertiesView.ctor FAILED [{depth}]: {current.GetType().Name}: {current.Message}");
                if (depth == 0 && ex.StackTrace is not null)
                {
                    SmartConLogger.Error($"Stack: {ex.StackTrace}");
                }
                current = current.InnerException;
                depth++;
            }
            throw;
        }
    }

    private void OnViewModelLoaded(object sender, RoutedEventArgs e)
    {
        SmartConLogger.Info("FamilyPropertiesView.OnViewModelLoaded: enter (Window.Loaded)");
        // 3D init is deferred to OnViewport3DXLoaded (fires when user opens 3D tab)
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// Called when Viewport3DX itself becomes loaded (i.e. when the user
    /// switches to the 3D tab). WPF TabControl lazy-renders non-active tabs,
    /// so the viewport's Loaded event fires only when the tab is first selected.
    /// At this point ActualWidth/Height > 0 and the DirectX render host can
    /// initialize its swap chain.
    /// </summary>
    private void OnViewport3DXLoaded(object sender, RoutedEventArgs e)
    {
        SmartConLogger.Info($"FamilyPropertiesView.OnViewport3DXLoaded: enter (ActualWidth={Properties3DViewport.ActualWidth})");
        try
        {
            if (DataContext is FamilyPropertiesViewModel vm)
            {
                vm.Initialize3DInfrastructure();

                // Only add Scene3DRoot when viewport has non-zero size
                // (render host D3D only starts when ActualWidth > 0)
                if (Properties3DViewport.ActualWidth > 0 && Properties3DViewport.ActualHeight > 0)
                {
                    if (!Properties3DViewport.Items.Contains(vm.Scene3DRoot))
                    {
                        Properties3DViewport.Items.Add(vm.Scene3DRoot);
                        SmartConLogger.Info("OnViewport3DXLoaded: Scene3DRoot added to Viewport3DX.Items");
                    }

                    _ = vm.Load3DPreviewAsync(System.Threading.CancellationToken.None);
                    SmartConLogger.Info("OnViewport3DXLoaded: Load3DPreviewAsync dispatched");
                }
                else
                {
                    SmartConLogger.Info("OnViewport3DXLoaded: viewport has zero size, deferring");
                }

                DumpViewportState();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FamilyPropertiesView.OnViewport3DXLoaded FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// DIAGNOSTIC: Dump the Viewport3DX control state — RenderHost,
    /// EffectsManager, Items count, ActualWidth/Height. This is read
    /// from code-behind because RenderHost is not a bindable property.
    /// </summary>
    private void DumpViewportState()
    {
        try
        {
            var vp = Properties3DViewport;
            if (vp is null)
            {
                SmartConLogger.Warn("DumpViewportState: Properties3DViewport is null [Action: XAML mc:AlternateContent may have selected the Fallback]");
                return;
            }

            var em = vp.GetType().GetProperty("EffectsManager")?.GetValue(vp);
            SmartConLogger.Info(
                $"DumpViewportState: Viewport3DX found. " +
                $"ActualWidth={vp.ActualWidth} ActualHeight={vp.ActualHeight} " +
                $"IsLoaded={vp.IsLoaded} " +
                $"EffectsManager={(em?.GetType().Name ?? "<null>")} " +
                $"Items.Count={vp.Items.Count} " +
                $"Background={vp.Background} BackgroundColor={vp.BackgroundColor}");

            for (int i = 0; i < vp.Items.Count; i++)
            {
                var item = vp.Items[i];
                SmartConLogger.Info($"  Viewport.Items[{i}]: {item?.GetType().Name ?? "<null>"}");

                if (item is HelixToolkit.Wpf.SharpDX.Element3DPresenter presenter)
                {
                    var content = presenter.Content;
                    SmartConLogger.Info($"    Element3DPresenter.Content: {content?.GetType().Name ?? "<null>"}");
                    if (content is HelixToolkit.Wpf.SharpDX.SceneNodeGroupModel3D group)
                    {
                        var sn = group.SceneNode;
                        SmartConLogger.Info(
                            $"    Scene3DRoot.SceneNode: Type={sn.GetType().Name} " +
                            $"IsAttached={sn.IsAttached} IsRenderable={sn.IsRenderable} " +
                            $"Items.Count={sn.Items.Count}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"DumpViewportState failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
#endif

    private void OnViewModelClosed(object? sender, System.EventArgs e)
    {
        SmartConLogger.Info("FamilyPropertiesView.OnViewModelClosed: enter");
        try
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
                SmartConLogger.Info("FamilyPropertiesView.OnViewModelClosed: Dispose done");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FamilyPropertiesView.OnViewModelClosed FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Programmatic header installation for the Versions tab DataGrid columns
    /// (I-12: DataGridColumn.Header is not a FrameworkElement — DynamicResource
    /// won't resolve when Application.Current is null in Revit's net48 host).
    /// </summary>
    private void InitializeVersionGridHeaders()
    {
        ColVersionLabel.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Version) ?? "Version";
        ColVersionRevit.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Revit) ?? "Revit";
        ColVersionDate.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Date) ?? "Date";
        ColVersionAuthor.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Author) ?? "Author";
        ColVersionTypes.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Types) ?? "Types";
        ColVersionActive.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Active) ?? "Status";
    }
}
