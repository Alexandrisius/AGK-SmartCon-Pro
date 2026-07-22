using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class SharedParameterPickerViewModel : ObservableObject, IObservableRequestClose
{
    private readonly ISharedParameterFileParser _parser;
    private readonly IFamilyManagerUserSettingsRepository _settingsRepository;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly HashSet<string> _existingNames;
    private List<SharedParameterItemViewModel> _allItems = [];

    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private ObservableCollection<SharedParameterItemViewModel> _items = [];
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _availableFopGroups = [];
    [ObservableProperty] private string? _selectedFopGroup;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isLoaded;

    public event Action<bool?>? RequestClose;

    public SharedParameterPickerViewModel(
        ISharedParameterFileParser parser,
        IFamilyManagerUserSettingsRepository settingsRepository,
        IFamilyManagerDialogService dialogService,
        IEnumerable<string> existingNames)
    {
        _parser = parser;
        _settingsRepository = settingsRepository;
        _dialogService = dialogService;
        _existingNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
    }

    public void Initialize()
    {
        FamilyManagerUserSettings settings;
        try
        {
            settings = _settingsRepository.Load();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"SharedParameterPicker settings load failed: {ex.Message} [Action: выберите файл общих параметров вручную]");
            return;
        }

        var cachedPath = settings.SharedParametersFilePath;
        if (string.IsNullOrWhiteSpace(cachedPath))
            return;

        FilePath = cachedPath!;
        if (File.Exists(FilePath))
        {
            LoadFromFile();
        }
        else
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SP_CachedFileMissing)
                ?? "Ранее выбранный файл не найден. Выберите файл заново.";
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedFopGroupChanged(string? value) => ApplyFilter();

    [RelayCommand]
    private void Browse()
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", nameof(Browse)));

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_SP_BrowseTitle)
            ?? "Выберите файл общих параметров";
        string? initialDir = null;
        if (!string.IsNullOrWhiteSpace(FilePath))
        {
            try
            {
                initialDir = Path.GetDirectoryName(FilePath);
            }
            catch (ArgumentException)
            {
                initialDir = null;
            }
        }

        var path = _dialogService.ShowOpenTextFileDialog(title, initialDir);
        if (path is null)
            return;

        FilePath = path;
        LoadFromFile();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
        {
            if (item.CanSelect)
                item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    [RelayCommand]
    private void Ok()
    {
        var actualSelected = _allItems.Count(i => i.IsSelected && !i.AlreadyExists);
        SmartConLogger.Debug($"SharedParameterPicker.Ok: SelectedCount={SelectedCount}, actualSelected={actualSelected}, total={_allItems.Count}");

        if (actualSelected == 0)
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SP_NothingSelected)
                ?? "Не выбрано ни одного атрибута";
            return;
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    public IReadOnlyList<SharedParameterEntry> GetSelectedEntries() =>
        _allItems
            .Where(i => i.IsSelected && !i.AlreadyExists)
            .Select(i => i.Entry)
            .ToList();

    private void LoadFromFile()
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", nameof(LoadFromFile)),
            ("FileName", Path.GetFileName(FilePath)));

        try
        {
            var entries = _parser.ParseFile(FilePath);
            TryCachePath();

            foreach (var old in _allItems)
                old.PropertyChanged -= OnItemPropertyChanged;

            _allItems = entries
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new SharedParameterItemViewModel(e, _existingNames.Contains(e.Name)))
                .ToList();

            foreach (var item in _allItems)
                item.PropertyChanged += OnItemPropertyChanged;

            IsLoaded = true;
            TotalCount = _allItems.Count;
            StatusMessage = _allItems.Count == 0
                ? LanguageManager.GetString(StringLocalization.Keys.FM_SP_NoParams) ?? "В файле не найдено параметров"
                : string.Empty;

            RefreshFopGroups();
            ApplyFilter();
            UpdateSelectedCount();
            SmartConLogger.Info($"SharedParameterPicker: loaded {_allItems.Count} parameters");
        }
        catch (Exception ex)
        {
            IsLoaded = false;
            _allItems = [];
            Items = [];
            AvailableFopGroups = [];
            TotalCount = 0;
            SelectedCount = 0;
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_SP_ParseError)
                    ?? "Не удалось прочитать файл общих параметров: {0}",
                ex.Message);
            SmartConLogger.Warn($"SharedParameterPicker parse failed: {ex.Message} [Action: проверьте, что выбран корректный файл общих параметров Revit (.txt)]");
        }
    }

    private void TryCachePath()
    {
        try
        {
            _settingsRepository.Save(new FamilyManagerUserSettings(FilePath));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"SharedParameterPicker settings save failed: {ex.Message} [Action: проигнорируйте; путь к ФОП не запомнится между сессиями]");
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SharedParameterItemViewModel.IsSelected))
            UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
        => SelectedCount = _allItems.Count(i => i.IsSelected && !i.AlreadyExists);

    private string AllGroupsLabel =>
        LanguageManager.GetString(StringLocalization.Keys.FM_SP_AllGroups) ?? "Все группы";

    private void RefreshFopGroups()
    {
        var groups = _allItems
            .Select(i => i.GroupName)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var all = new List<string> { AllGroupsLabel };
        all.AddRange(groups);
        AvailableFopGroups = new ObservableCollection<string>(all);
        SelectedFopGroup = AllGroupsLabel;
    }

    private void ApplyFilter()
    {
        IEnumerable<SharedParameterItemViewModel> filtered = _allItems;

        if (!string.IsNullOrWhiteSpace(SelectedFopGroup) && SelectedFopGroup != AllGroupsLabel)
        {
            filtered = filtered.Where(i => i.GroupName == SelectedFopGroup);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var f = SearchText.Trim();
            filtered = filtered.Where(i =>
                ContainsIgnoreCase(i.Name, f) ||
                (i.GroupName is not null && ContainsIgnoreCase(i.GroupName, f)) ||
                (i.Description is not null && ContainsIgnoreCase(i.Description, f)));
        }

        Items = new ObservableCollection<SharedParameterItemViewModel>(filtered);
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
#if NETFRAMEWORK
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#else
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#endif
    }
}

public sealed partial class SharedParameterItemViewModel : ObservableObject
{
    public SharedParameterItemViewModel(SharedParameterEntry entry, bool alreadyExists)
    {
        Entry = entry;
        AlreadyExists = alreadyExists;
    }

    public SharedParameterEntry Entry { get; }
    public bool AlreadyExists { get; }
    public bool CanSelect => !AlreadyExists;
    public string Name => Entry.Name;
    public string DataType => Entry.DataType;
    public string? GroupName => Entry.GroupName;
    public string? Description => Entry.Description;

    [ObservableProperty] private bool _isSelected;
}
