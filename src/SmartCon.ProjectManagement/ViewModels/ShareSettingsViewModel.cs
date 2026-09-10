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
    private readonly IDialogService _dialogService;
    private readonly IDialogPresenter _dialogPresenter;
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
        IDialogService dialogService,
        IDialogPresenter dialogPresenter,
        Autodesk.Revit.DB.Document doc)
    {
        _repository = repository;
        _viewRepository = viewRepository;
        _fileNameParser = fileNameParser;
        _dialogService = dialogService;
        _dialogPresenter = dialogPresenter;
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
        using var _scope = SmartConLogger.BeginScope("ShareSettings",
            ("Method", "LoadFromFile"));
        if (_doc is null) return;

        CurrentFilePath = _doc.PathName ?? string.Empty;
        CurrentFolder = string.IsNullOrEmpty(CurrentFilePath) ? string.Empty : Path.GetDirectoryName(CurrentFilePath) ?? string.Empty;
        CurrentFileName = string.IsNullOrEmpty(_doc.PathName)
            ? (_doc.Title ?? string.Empty)
            : Path.GetFileName(_doc.PathName);

        SmartConLogger.Info($"ShareSettingsViewModel loading. File='{CurrentFileName}'");

        var settings = _repository.Load(_doc);

        ShareFolderPath = settings.ShareFolderPath;
        SyncBeforeShare = settings.SyncBeforeShare;

        ApplyPurgeOptions(settings.PurgeOptions);

        Blocks.CollectionChanged += (_, _) => RefreshPreview();
        ExportMappings.CollectionChanged += (_, _) => RefreshPreview();

        FieldLibrary.Clear();
        foreach (var fd in settings.FieldLibrary)
            FieldLibrary.Add(fd);
        OnPropertyChanged(nameof(FieldNames));

        LoadBlocksFromSettings(settings.FileNameTemplate);
        LoadExportMappingsFromSettings(settings.FileNameTemplate);

        _pendingKeepViewNames = settings.KeepViewNames.ToHashSet();

        SmartConLogger.Info($"Loaded settings: ShareFolder='{ShareFolderPath}', Blocks={Blocks.Count}, ExportMappings={ExportMappings.Count}, FieldLibrary={FieldLibrary.Count}");

        AutoParseFileName();
        RefreshPreview();
        RefreshMappingValidation();
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
        if (e.PropertyName is nameof(ExportMappingItem.Field) or nameof(ExportMappingItem.TargetValue))
        {
            RefreshMappingValidation();
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

    private void RefreshMappingValidation()
    {
        foreach (var mapping in ExportMappings)
        {
            var fieldDef = FieldLibrary.FirstOrDefault(fd =>
                string.Equals(fd.Name, mapping.Field, StringComparison.OrdinalIgnoreCase));

            if (fieldDef is not null && fieldDef.AllowedValues.Count > 0)
            {
                mapping.AllowedTargetValues.Clear();
                foreach (var av in fieldDef.AllowedValues)
                    mapping.AllowedTargetValues.Add(av);
            }
            else
            {
                mapping.AllowedTargetValues.Clear();
            }

            if (string.IsNullOrEmpty(mapping.Field) || string.IsNullOrWhiteSpace(mapping.TargetValue))
            {
                mapping.IsValid = true;
                mapping.ValidationError = null;
                continue;
            }

            if (fieldDef is null || fieldDef.ValidationMode == ValidationMode.None)
            {
                mapping.IsValid = true;
                mapping.ValidationError = null;
                continue;
            }

            var (valid, error) = _fileNameParser.ValidateSingleValue(mapping.TargetValue, fieldDef);
            mapping.IsValid = valid;
            mapping.ValidationError = valid ? null : error;
        }
    }

    [RelayCommand]
    private void BrowseSharedFolder()
    {
        var path = _dialogService.ShowFolderBrowser(
            LocalizationService.GetString("PM_BrowseShareFolder"),
            ShareFolderPath);
        if (path is not null)
            ShareFolderPath = path;
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

        SmartConLogger.Info($"RefreshViews: savedNames={savedNames.Count}");

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
    private void Ok()
    {
        try
        {
            var settings = BuildSettings();
            _repository.Save(_doc, settings);
            SmartConLogger.Info($"Settings saved. ShareFolder='{settings.ShareFolderPath}', Blocks={settings.FileNameTemplate.Blocks.Count}, KeepViews={settings.KeepViewNames.Count}");
            StatusMessage = LocalizationService.GetString("Btn_Saved");
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Save failed: {ex.Message}");
            _dialogService.ShowError("Error", $"Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

}

