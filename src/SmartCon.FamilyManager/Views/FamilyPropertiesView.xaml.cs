using System.Windows;
using System.Windows.Input;
using HelixToolkit.Wpf.SharpDX;
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

    /// <summary>
    /// Called when Viewport3DX itself becomes loaded (i.e. when the user
    /// switches to the 3D tab). WPF TabControl lazy-renders non-active tabs,
    /// so the viewport's Loaded event fires only when the tab is first selected.
    /// At this point ActualWidth/Height > 0 and the DirectX render host can
    /// initialize its swap chain.
    /// </summary>
    private void OnViewport3DXLoaded(object sender, RoutedEventArgs e)
    {
        SmartConLogger.Info($"FamilyPropertiesView.OnViewport3DXLoaded: enter (ActualWidth={Properties3DViewport.ActualWidth}, ActualHeight={Properties3DViewport.ActualHeight})");
        try
        {
            if (DataContext is FamilyPropertiesViewModel vm)
            {
                SmartConLogger.Info(
                    $"OnViewport3DXLoaded: vm.Selected3DTypeName={vm.Selected3DTypeName ?? "<null>"}, " +
                    $"EffectsManager3DIsNull={vm.EffectsManager3D is null}");

                // Only initialize DirectX infrastructure when viewport has
                // non-zero size. WPF TabControl lazy-renders non-active tabs,
                // so the viewport's Loaded event fires first with ActualWidth=0
                // and then again when the 3D tab is selected. Initializing
                // EffectsManager for a zero-size surface creates a DirectX
                // device that is not yet attached to a render target and makes
                // Load3DPreviewForTypeAsync think the viewer is ready.
                if (Properties3DViewport.ActualWidth > 0 && Properties3DViewport.ActualHeight > 0)
                {
                    vm.Initialize3DInfrastructure();

                    if (!Properties3DViewport.Items.Contains(vm.Scene3DRoot))
                    {
                        Properties3DViewport.Items.Add(vm.Scene3DRoot);
                        SmartConLogger.Info("OnViewport3DXLoaded: Scene3DRoot added to Viewport3DX.Items");
                    }

                    // FitCameraToScene() is called inside Load3DPreviewForTypeAsync (VM).
                    _ = vm.Load3DPreviewForTypeAsync(vm.Selected3DTypeName, System.Threading.CancellationToken.None);
                    SmartConLogger.Info("OnViewport3DXLoaded: Load3DPreviewForTypeAsync dispatched (FitCameraToScene handles camera)");
                }
                else
                {
                    SmartConLogger.Info("OnViewport3DXLoaded: viewport has zero size, deferring");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FamilyPropertiesView.OnViewport3DXLoaded FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

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
        ColVersionFileName.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_FileName) ?? "Family name";
        ColVersionRevit.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Revit) ?? "Revit";
        ColVersionDate.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Date) ?? "Date";
        ColVersionAuthor.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Author) ?? "Author";
        ColVersionTypes.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Types) ?? "Types";
        ColVersionActive.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Active) ?? "Status";
    }

    /// <summary>
    /// PreviewTextInput for the tag input TextBox. Suppresses the comma character
    /// and immediately commits the current text as a tag chip (Gmail/GitHub-style).
    /// PreviewTextInput (not PreviewKeyDown) is used because Key.OemComma is a
    /// physical key, not a character: on the Russian layout the same key produces
    /// the letter 'б', so key-based detection swallowed 'б' and created a chip.
    /// TextCompositionEventArgs.Text carries the layout-translated character, so
    /// only a real ',' commits the tag. e.Handled=true prevents the comma from
    /// appearing. Pasted commas never raise TextInput — they are split inside
    /// FamilyPropertiesViewModel.AddTagCommand. This is UI input plumbing (not
    /// business logic) — validation and collection update live in the ViewModel.
    /// </summary>
    private void TagInputBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (e.Text != ",") return;
        if (DataContext is not FamilyPropertiesViewModel vm) return;
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(TagInputBox.Text))
            vm.AddTagCommand.Execute(TagInputBox.Text);
    }
}
