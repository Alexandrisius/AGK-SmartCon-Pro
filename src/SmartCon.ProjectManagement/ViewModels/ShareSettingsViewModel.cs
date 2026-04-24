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
    private Window? _ownerWindow;

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
    public ObservableCollection<ExportMappingItem> ExportMappings { get; } = [];
    public ObservableCollection<ViewSelectionItem> Views { get; } = [];
    public ObservableCollection<FieldDefinition> FieldLibrary { get; } = [];

    public List<string> FieldNames => FieldLibrary.Select(f => f.Name).ToList();

    public ICollectionView FilteredViews { get; }

    public int SelectedCount => Views.Count(v => v.IsSelected);

    public event Action<bool?>? RequestClose;

    public void SetOwnerWindow(Window? window) => _ownerWindow = window;

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

        Blocks.CollectionChanged += (_, _) => RefreshPreview();
        ExportMappings.CollectionChanged += (_, _) => RefreshPreview();

        FieldLibrary.Clear();
        foreach (var fd in settings.FieldLibrary)
            FieldLibrary.Add(fd);
        OnPropertyChanged(nameof(FieldNames));

        Blocks.Clear();
        foreach (var b in settings.FileNameTemplate.Blocks)
        {
            var item = new FileNameBlockItem
            {
                Index = b.Index,
                Field = b.Field,
                ParseRule = b.ParseRule
            };
            item.PropertyChanged += OnBlockItemChanged;
            Blocks.Add(item);
        }

        ExportMappings.Clear();
        foreach (var m in settings.FileNameTemplate.ExportMappings)
        {
            var item = new ExportMappingItem
            {
                Field = m.Field,
                SourceValue = m.SourceValue,
                TargetValue = m.TargetValue
            };
            item.PropertyChanged += OnMappingItemChanged;
            ExportMappings.Add(item);
        }

        _pendingKeepViewNames = settings.KeepViewNames.ToHashSet();

        SmartConLogger.Info($"[PM] Loaded settings: ShareFolder='{ShareFolderPath}', Blocks={Blocks.Count}, ExportMappings={ExportMappings.Count}, FieldLibrary={FieldLibrary.Count}");

        AutoParseFileName();
        RefreshPreview();
    }

    private void OnBlockItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileNameBlockItem.Field))
        {
            AutoParseFileName();
            UpdateMappingCurrentValues();
        }
        RefreshValidation();
        RefreshPreview();
    }

    private void OnMappingItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportMappingItem.Field))
        {
            UpdateMappingCurrentValues();
            AutoParseFileName();
        }
        RefreshPreview();
    }

    private void AutoParseFileName()
    {
        if (string.IsNullOrEmpty(CurrentFileName) || Blocks.Count == 0) return;

        var template = BuildTemplate();
        var parsed = _fileNameParser.ParseBlocks(CurrentFileName, template);

        foreach (var block in Blocks)
        {
            if (parsed.TryGetValue(block.Field, out var value))
                block.CurrentFieldValue = value;
            else
                block.CurrentFieldValue = string.Empty;
        }

        UpdateMappingCurrentValues();
        RefreshValidation();
    }

    private void UpdateMappingCurrentValues()
    {
        var template = BuildTemplate();
        var parsed = _fileNameParser.ParseBlocks(CurrentFileName, template);

        foreach (var mapping in ExportMappings)
        {
            var blockValue = parsed.TryGetValue(mapping.Field, out var v) ? v : string.Empty;
            mapping.CurrentBlockValue = blockValue;
            mapping.SourceValue = blockValue;
        }
    }

    private void RefreshValidation()
    {
        var template = BuildTemplate();
        if (string.IsNullOrEmpty(CurrentFileName) || template.Blocks.Count == 0) return;

        var validation = _fileNameParser.ValidateDetailed(CurrentFileName, template, FieldLibrary.ToList());

        foreach (var bv in validation.Blocks)
        {
            var blockItem = Blocks.FirstOrDefault(b => b.Index == bv.Index);
            if (blockItem is null) continue;
            blockItem.IsValid = bv.IsValid;
            blockItem.ValidationError = bv.Error;
        }
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
    private void AddBlock()
    {
        var index = Blocks.Count > 0 ? Blocks.Max(b => b.Index) + 1 : 0;
        var item = new FileNameBlockItem
        {
            Index = index,
            Field = string.Empty,
            ParseRule = ParseRule.DefaultDelimiter("-", index)
        };
        item.PropertyChanged += OnBlockItemChanged;
        Blocks.Add(item);
        RefreshValidation();
        RefreshPreview();
    }

    [RelayCommand]
    private void RemoveBlock()
    {
        if (SelectedBlockIndex >= 0 && SelectedBlockIndex < Blocks.Count)
        {
            Blocks.RemoveAt(SelectedBlockIndex);
            RefreshValidation();
            RefreshPreview();
        }
    }

    [RelayCommand]
    private void OpenParseRuleEditor()
    {
        if (SelectedBlockIndex < 0 || SelectedBlockIndex >= Blocks.Count) return;

        var block = Blocks[SelectedBlockIndex];
        var precedingRules = Blocks
            .OrderBy(b => b.Index)
            .TakeWhile(b => b.Index != block.Index)
            .Select(b => b.ParseRule)
            .ToList();

        var originalRule = block.ParseRule;

        try
        {
            SmartConLogger.Info("[PM] OpenParseRuleEditor: creating ViewModel");
            var vm = new ParseRuleViewModel(block.ParseRule, CurrentFileName, precedingRules);
            SmartConLogger.Info("[PM] OpenParseRuleEditor: ViewModel created, creating View");
            var owner = GetOwnerWindow();
            SmartConLogger.Info($"[PM] OpenParseRuleEditor: owner={(owner?.GetType().Name ?? "null")}");
            var view = new Views.ParseRuleView(vm) { Owner = owner };
            SmartConLogger.Info("[PM] OpenParseRuleEditor: calling ShowDialog");
            view.ShowDialog();
            SmartConLogger.Info("[PM] OpenParseRuleEditor: ShowDialog returned");

            if (view.CustomDialogResult != true) return;

            block.ParseRule = vm.BuildRule();
            block.RefreshParseRuleDisplay();
            AutoParseFileName();
            RefreshPreview();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[PM] OpenParseRuleEditor failed:\n{ex}");
            System.Windows.MessageBox.Show($"OpenParseRuleEditor error:\n\n{ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void AddExportMapping()
    {
        var item = new ExportMappingItem();
        item.PropertyChanged += OnMappingItemChanged;
        ExportMappings.Add(item);
        UpdateMappingCurrentValues();
    }

    [RelayCommand]
    private void RemoveExportMapping()
    {
        if (SelectedMappingIndex >= 0 && SelectedMappingIndex < ExportMappings.Count)
            ExportMappings.RemoveAt(SelectedMappingIndex);
    }

    [RelayCommand]
    private void OpenFieldLibrary()
    {
        try
        {
            SmartConLogger.Info("[PM] OpenFieldLibrary: creating ViewModel");
            var vm = new FieldLibraryViewModel();
            foreach (var fd in FieldLibrary)
                vm.Fields.Add(FieldDefinitionItem.FromModel(fd));

            SmartConLogger.Info($"[PM] OpenFieldLibrary: {vm.Fields.Count} fields, creating View");
            var owner = GetOwnerWindow();
            SmartConLogger.Info($"[PM] OpenFieldLibrary: owner={(owner?.GetType().Name ?? "null")}");
            var view = new Views.FieldLibraryView(vm) { Owner = owner };
            SmartConLogger.Info("[PM] OpenFieldLibrary: calling ShowDialog");
            view.ShowDialog();
            SmartConLogger.Info("[PM] OpenFieldLibrary: ShowDialog returned");

            if (view.CustomDialogResult != true) return;

            FieldLibrary.Clear();
            foreach (var item in vm.Fields)
                FieldLibrary.Add(item.ToModel());
            OnPropertyChanged(nameof(FieldNames));

            AutoParseFileName();
            RefreshValidation();
            RefreshPreview();

            SmartConLogger.Info($"[PM] FieldLibrary updated: {FieldLibrary.Count} definitions");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[PM] OpenFieldLibrary failed:\n{ex}");
            System.Windows.MessageBox.Show($"OpenFieldLibrary error:\n\n{ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Window? GetOwnerWindow()
    {
        if (_ownerWindow is not null) return _ownerWindow;
        return System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            Title = "Import Settings from File"
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
            FileName = "smartcon-export-settings.json",
            Title = "Export Settings to File"
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
            RequestClose?.Invoke(true);
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
        RequestClose?.Invoke(false);
    }

    private FileNameTemplate BuildTemplate()
    {
        return new FileNameTemplate
        {
            Blocks = Blocks.Select(b => new FileBlockDefinition
            {
                Index = b.Index,
                Field = b.Field,
                ParseRule = b.ParseRule
            }).ToList(),
            ExportMappings = ExportMappings.Select(m => new ExportMapping
            {
                Field = m.Field,
                SourceValue = m.SourceValue,
                TargetValue = m.TargetValue
            }).ToList()
        };
    }

    private ShareProjectSettings BuildSettings()
    {
        return new ShareProjectSettings
        {
            ShareFolderPath = ShareFolderPath,
            SyncBeforeShare = SyncBeforeShare,
            FieldLibrary = FieldLibrary.ToList(),
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
            FileNameTemplate = BuildTemplate()
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

        FieldLibrary.Clear();
        foreach (var fd in settings.FieldLibrary)
            FieldLibrary.Add(fd);
        OnPropertyChanged(nameof(FieldNames));

        Blocks.Clear();
        foreach (var b in settings.FileNameTemplate.Blocks)
        {
            var item = new FileNameBlockItem
            {
                Index = b.Index,
                Field = b.Field,
                ParseRule = b.ParseRule
            };
            item.PropertyChanged += OnBlockItemChanged;
            Blocks.Add(item);
        }

        ExportMappings.Clear();
        foreach (var m in settings.FileNameTemplate.ExportMappings)
        {
            var item = new ExportMappingItem
            {
                Field = m.Field,
                SourceValue = m.SourceValue,
                TargetValue = m.TargetValue
            };
            item.PropertyChanged += OnMappingItemChanged;
            ExportMappings.Add(item);
        }

        _pendingKeepViewNames = settings.KeepViewNames.ToHashSet();

        if (Views.Count > 0)
        {
            var savedViewNames = settings.KeepViewNames.ToHashSet();
            foreach (var v in Views)
                v.IsSelected = savedViewNames.Contains(v.Name);
            OnPropertyChanged(nameof(SelectedCount));
        }

        AutoParseFileName();
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

        var template = BuildTemplate();
        var shared = _fileNameParser.TransformForExport(CurrentFileName, template, FieldLibrary.ToList());
        PreviewShared = shared ?? "(invalid)";
    }
}
