using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ShareSettingsViewModel : ObservableObject, IObservableRequestClose
{
    private readonly IShareProjectSettingsRepository _repository;
    private readonly IViewRepository _viewRepository;
    private readonly IFileNameParser _fileNameParser;
    private readonly Autodesk.Revit.DB.Document _doc;
    private HashSet<string> _pendingKeepViewNames = [];
    private bool _viewsLoaded;

    [ObservableProperty]
    private string _currentFilePath = string.Empty;

    [ObservableProperty]
    private string _currentFolder = string.Empty;

    [ObservableProperty]
    private string _currentFileName = string.Empty;

    [ObservableProperty]
    private string _shareFolderPath = string.Empty;

    [ObservableProperty]
    private bool _syncBeforeShare = true;

    [ObservableProperty]
    private bool _purgeRvtLinks = true;

    [ObservableProperty]
    private bool _purgeCadImports = true;

    [ObservableProperty]
    private bool _purgeImages = true;

    [ObservableProperty]
    private bool _purgePointClouds = true;

    [ObservableProperty]
    private bool _purgeGroups = true;

    [ObservableProperty]
    private bool _purgeAssemblies = true;

    [ObservableProperty]
    private bool _purgeSpaces = true;

    [ObservableProperty]
    private bool _purgeRebar = true;

    [ObservableProperty]
    private bool _purgeFabricReinforcement = true;

    [ObservableProperty]
    private bool _purgeSheets = true;

    [ObservableProperty]
    private bool _purgeSchedules = true;

    [ObservableProperty]
    private bool _purgeUnused = true;

    [ObservableProperty]
    private string _delimiter = "-";

    [ObservableProperty]
    private string _previewCurrent = string.Empty;

    [ObservableProperty]
    private string _previewShared = string.Empty;

    [ObservableProperty]
    private int _selectedBlockIndex = -1;

    [ObservableProperty]
    private int _selectedMappingIndex = -1;

    [ObservableProperty]
    private int _selectedViewIndex = -1;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _viewSearchText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ObservableCollection<FileNameBlockItem> Blocks { get; } = [];
    public ObservableCollection<StatusMappingItem> StatusMappings { get; } = [];
    public ObservableCollection<ViewSelectionItem> Views { get; } = [];

    public ICollectionView FilteredViews { get; }

    public IReadOnlyList<string> PredefinedRoles { get; } = FileBlockDefinition.PredefinedRoles.ToList();

    public int SelectedCount => Views.Count(v => v.IsSelected);

    public event Action? RequestClose;

    public ShareSettingsViewModel(
        IShareProjectSettingsRepository repository,
        IViewRepository viewRepository,
        IFileNameParser fileNameParser,
        Autodesk.Revit.DB.Document doc)
    {
        _repository = repository;
        _viewRepository = viewRepository;
        _fileNameParser = fileNameParser;
        _doc = doc;

        FilteredViews = CollectionViewSource.GetDefaultView(Views);
        FilteredViews.Filter = o => o is ViewSelectionItem v && PassesFilter(v);
        FilteredViews.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ViewSelectionItem.ViewType)));

        LoadFromFile();
    }

    partial void OnDelimiterChanged(string value) => RefreshPreview();

    partial void OnViewSearchTextChanged(string value) => FilteredViews.Refresh();

    private bool PassesFilter(ViewSelectionItem item)
    {
        if (string.IsNullOrWhiteSpace(ViewSearchText)) return true;
#if NETFRAMEWORK
        var search = ViewSearchText.ToLowerInvariant();
        return item.Name.ToLowerInvariant().Contains(search)
            || item.ViewType.ToLowerInvariant().Contains(search);
#else
        return item.Name.Contains(ViewSearchText, StringComparison.OrdinalIgnoreCase)
            || item.ViewType.Contains(ViewSearchText, StringComparison.OrdinalIgnoreCase);
#endif
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 2 && !_viewsLoaded)
            RefreshViews();
    }

    private void LoadFromFile()
    {
        if (_doc is null) return;

        CurrentFilePath = _doc.PathName ?? string.Empty;
        CurrentFolder = string.IsNullOrEmpty(CurrentFilePath) ? string.Empty : Path.GetDirectoryName(CurrentFilePath) ?? string.Empty;
        CurrentFileName = _doc.Title ?? string.Empty;

        SmartConLogger.Info($"[PM] ShareSettingsViewModel loading. File='{CurrentFileName}'");

        var settings = _repository.Load(_doc);

        ShareFolderPath = settings.ShareFolderPath;
        SyncBeforeShare = settings.SyncBeforeShare;

        var po = settings.PurgeOptions;
        PurgeRvtLinks = po.PurgeRvtLinks;
        PurgeCadImports = po.PurgeCadImports;
        PurgeImages = po.PurgeImages;
        PurgePointClouds = po.PurgePointClouds;
        PurgeGroups = po.PurgeGroups;
        PurgeAssemblies = po.PurgeAssemblies;
        PurgeSpaces = po.PurgeSpaces;
        PurgeRebar = po.PurgeRebar;
        PurgeFabricReinforcement = po.PurgeFabricReinforcement;
        PurgeSheets = po.PurgeSheets;
        PurgeSchedules = po.PurgeSchedules;
        PurgeUnused = po.PurgeUnused;

        Delimiter = settings.FileNameTemplate.Delimiter;

        Blocks.CollectionChanged += (_, _) => RefreshPreview();
        StatusMappings.CollectionChanged += (_, _) => RefreshPreview();

        Blocks.Clear();
        foreach (var b in settings.FileNameTemplate.Blocks)
        {
            var item = new FileNameBlockItem { Index = b.Index, Role = b.Role, Label = b.Label };
            item.PropertyChanged += (_, _) => RefreshPreview();
            Blocks.Add(item);
        }

        StatusMappings.Clear();
        foreach (var m in settings.FileNameTemplate.StatusMappings)
        {
            var item = new StatusMappingItem { WipValue = m.WipValue, SharedValue = m.SharedValue };
            item.PropertyChanged += (_, _) => RefreshPreview();
            StatusMappings.Add(item);
        }

        _pendingKeepViewNames = settings.KeepViewNames.ToHashSet();

        SmartConLogger.Info($"[PM] Loaded settings: ShareFolder='{ShareFolderPath}', Blocks={Blocks.Count}, Mappings={StatusMappings.Count}, KeepViews={_pendingKeepViewNames.Count}");

        RefreshPreview();
    }

    [RelayCommand]
    private void BrowseSharedFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = ShareFolderPath
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ShareFolderPath = dialog.SelectedPath;
    }

    [RelayCommand]
    private void SelectAll()
    {
        PurgeRvtLinks = PurgeCadImports = PurgeImages = PurgePointClouds = true;
        PurgeGroups = PurgeAssemblies = PurgeSpaces = PurgeRebar = PurgeFabricReinforcement = true;
        PurgeSheets = PurgeSchedules = PurgeUnused = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        PurgeRvtLinks = PurgeCadImports = PurgeImages = PurgePointClouds = false;
        PurgeGroups = PurgeAssemblies = PurgeSpaces = PurgeRebar = PurgeFabricReinforcement = false;
        PurgeSheets = PurgeSchedules = PurgeUnused = false;
    }

    private void RefreshViews()
    {
        if (_doc is null) return;

        var savedNames = Views.Count > 0
            ? Views.Where(v => v.IsSelected).Select(v => v.Name).ToHashSet()
            : _pendingKeepViewNames;

        SmartConLogger.Info($"[PM] RefreshViews: savedNames={savedNames.Count}");

        Views.Clear();
        var viewInfos = _viewRepository.GetAllViews(_doc);
        foreach (var vi in viewInfos)
        {
            Views.Add(new ViewSelectionItem
            {
                IsSelected = savedNames.Contains(vi.Name),
                Name = vi.Name,
                Id = vi.Id.ToString(),
                ViewType = vi.ViewType
            });
        }

        _pendingKeepViewNames = [];
        _viewsLoaded = true;

        foreach (var v in Views)
            v.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectedCount));

        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private void ParseFromFileName()
    {
        if (string.IsNullOrEmpty(CurrentFileName) || string.IsNullOrEmpty(Delimiter)) return;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(CurrentFileName);
        var parts = nameWithoutExt.Split([Delimiter], StringSplitOptions.None);

        Blocks.Clear();
        for (var i = 0; i < parts.Length; i++)
        {
            var role = i == parts.Length - 1 ? "status" : "custom";
            var item = new FileNameBlockItem { Index = i, Role = role, Label = string.Empty };
            item.PropertyChanged += (_, _) => RefreshPreview();
            Blocks.Add(item);
        }

        SmartConLogger.Info($"[PM] ParseFromFileName: {parts.Length} blocks from '{CurrentFileName}' with delimiter '{Delimiter}'");
        RefreshPreview();
    }

    [RelayCommand]
    private void AddBlock()
    {
        var index = Blocks.Count > 0 ? Blocks.Max(b => b.Index) + 1 : 0;
        var item = new FileNameBlockItem { Index = index, Role = "custom", Label = string.Empty };
        item.PropertyChanged += (_, _) => RefreshPreview();
        Blocks.Add(item);
        RefreshPreview();
    }

    [RelayCommand]
    private void RemoveBlock()
    {
        if (SelectedBlockIndex >= 0 && SelectedBlockIndex < Blocks.Count)
        {
            Blocks.RemoveAt(SelectedBlockIndex);
            RefreshPreview();
        }
    }

    [RelayCommand]
    private void AddStatusMapping()
    {
        var item = new StatusMappingItem();
        item.PropertyChanged += (_, _) => RefreshPreview();
        StatusMappings.Add(item);
    }

    [RelayCommand]
    private void RemoveStatusMapping()
    {
        if (SelectedMappingIndex >= 0 && SelectedMappingIndex < StatusMappings.Count)
            StatusMappings.RemoveAt(SelectedMappingIndex);
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            Title = "Import Share Settings"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var settings = _repository.ImportFromJson(json);
            LoadFromSettings(settings);
            SmartConLogger.Info($"[PM] Imported settings from {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[PM] Import failed: {ex.Message}");
            System.Windows.MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ExportSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = "smartcon-share-settings.json",
            Title = "Export Share Settings"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var settings = BuildSettings();
            var json = _repository.ExportToJson(settings);
            File.WriteAllText(dialog.FileName, json);
            SmartConLogger.Info($"[PM] Exported settings to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[PM] Export failed: {ex.Message}");
            System.Windows.MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var settings = BuildSettings();
            _repository.Save(_doc, settings);
            SmartConLogger.Info($"[PM] Settings saved. ShareFolder='{settings.ShareFolderPath}', Blocks={settings.FileNameTemplate.Blocks.Count}, KeepViews={settings.KeepViewNames.Count}");
            StatusMessage = LocalizationService.GetString("Btn_Saved");
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[PM] Save failed: {ex.Message}");
            System.Windows.MessageBox.Show($"Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    private ShareProjectSettings BuildSettings()
    {
        return new ShareProjectSettings
        {
            ShareFolderPath = ShareFolderPath,
            SyncBeforeShare = SyncBeforeShare,
            PurgeOptions = new PurgeOptions
            {
                PurgeRvtLinks = PurgeRvtLinks,
                PurgeCadImports = PurgeCadImports,
                PurgeImages = PurgeImages,
                PurgePointClouds = PurgePointClouds,
                PurgeGroups = PurgeGroups,
                PurgeAssemblies = PurgeAssemblies,
                PurgeSpaces = PurgeSpaces,
                PurgeRebar = PurgeRebar,
                PurgeFabricReinforcement = PurgeFabricReinforcement,
                PurgeSheets = PurgeSheets,
                PurgeSchedules = PurgeSchedules,
                PurgeUnused = PurgeUnused
            },
            KeepViewNames = Views.Where(v => v.IsSelected).Select(v => v.Name).ToList(),
            FileNameTemplate = new FileNameTemplate
            {
                Delimiter = Delimiter,
                Blocks = Blocks.Select(b => new FileBlockDefinition { Index = b.Index, Role = b.Role, Label = b.Label }).ToList(),
                StatusMappings = StatusMappings.Select(m => new StatusMapping { WipValue = m.WipValue, SharedValue = m.SharedValue }).ToList()
            }
        };
    }

    private void LoadFromSettings(ShareProjectSettings settings)
    {
        ShareFolderPath = settings.ShareFolderPath;
        SyncBeforeShare = settings.SyncBeforeShare;

        var po = settings.PurgeOptions;
        PurgeRvtLinks = po.PurgeRvtLinks;
        PurgeCadImports = po.PurgeCadImports;
        PurgeImages = po.PurgeImages;
        PurgePointClouds = po.PurgePointClouds;
        PurgeGroups = po.PurgeGroups;
        PurgeAssemblies = po.PurgeAssemblies;
        PurgeSpaces = po.PurgeSpaces;
        PurgeRebar = po.PurgeRebar;
        PurgeFabricReinforcement = po.PurgeFabricReinforcement;
        PurgeSheets = po.PurgeSheets;
        PurgeSchedules = po.PurgeSchedules;
        PurgeUnused = po.PurgeUnused;

        Delimiter = settings.FileNameTemplate.Delimiter;

        Blocks.Clear();
        foreach (var b in settings.FileNameTemplate.Blocks)
        {
            var item = new FileNameBlockItem { Index = b.Index, Role = b.Role, Label = b.Label };
            item.PropertyChanged += (_, _) => RefreshPreview();
            Blocks.Add(item);
        }

        StatusMappings.Clear();
        foreach (var m in settings.FileNameTemplate.StatusMappings)
        {
            var item = new StatusMappingItem { WipValue = m.WipValue, SharedValue = m.SharedValue };
            item.PropertyChanged += (_, _) => RefreshPreview();
            StatusMappings.Add(item);
        }

        _pendingKeepViewNames = settings.KeepViewNames.ToHashSet();

        if (Views.Count > 0)
        {
            var savedViewNames = settings.KeepViewNames.ToHashSet();
            foreach (var v in Views)
                v.IsSelected = savedViewNames.Contains(v.Name);
            OnPropertyChanged(nameof(SelectedCount));
        }

        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (string.IsNullOrEmpty(CurrentFileName))
        {
            PreviewCurrent = PreviewShared = string.Empty;
            return;
        }

        PreviewCurrent = CurrentFileName;

        var template = new FileNameTemplate
        {
            Delimiter = Delimiter,
            Blocks = Blocks.Select(b => new FileBlockDefinition { Index = b.Index, Role = b.Role, Label = b.Label }).ToList(),
            StatusMappings = StatusMappings.Select(m => new StatusMapping { WipValue = m.WipValue, SharedValue = m.SharedValue }).ToList()
        };

        var shared = _fileNameParser.TransformStatus(CurrentFileName, template);
        PreviewShared = shared ?? "(invalid)";
    }
}
