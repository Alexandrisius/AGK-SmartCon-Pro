using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Storage;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class MappingEditorViewModel : ObservableObject
{
    private readonly IFittingMappingRepository _repository;
    private readonly IDialogService _dialogService;

    public ObservableCollection<ConnectorTypeItem> ConnectorTypes { get; } = [];
    public ObservableCollection<MappingRuleItem> MappingRules { get; } = [];

    public IReadOnlyList<string> AvailableFamilyNames { get; }

    [ObservableProperty]
    private ConnectorTypeItem? _selectedType;

    [ObservableProperty]
    private MappingRuleItem? _selectedRule;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTypesSaved;

    [ObservableProperty]
    private bool _isRulesSaved;

    public MappingEditorViewModel(
        IFittingMappingRepository repository,
        IReadOnlyList<string> availableFamilyNames,
        IDialogService dialogService)
    {
        _repository = repository;
        _dialogService = dialogService;
        AvailableFamilyNames = availableFamilyNames;

        foreach (var t in _repository.GetConnectorTypes())
            ConnectorTypes.Add(ConnectorTypeItem.From(t));

        foreach (var r in _repository.GetMappingRules())
            MappingRules.Add(MappingRuleItem.From(r, _dialogService, AvailableFamilyNames));
    }

    [RelayCommand]
    private void AddType()
    {
        var next = ConnectorTypes.Count > 0 ? ConnectorTypes.Max(t => t.Code) + 1 : 1;
        var item = new ConnectorTypeItem { Code = next, Name = LocalizationService.GetString("Mapping_NewType") };
        ConnectorTypes.Add(item);
        SelectedType = item;
    }

    [RelayCommand]
    private void DeleteType()
    {
        if (SelectedType is not null)
            ConnectorTypes.Remove(SelectedType);
    }

    [RelayCommand]
    private async Task SaveTypes()
    {
        try
        {
            _repository.SaveConnectorTypes(ConnectorTypes.Select(t => t.ToDefinition()).ToList());
            _repository.SaveMappingRules(MappingRules.Select(r => r.ToRule()).ToList());
            IsTypesSaved = true;
            await Task.Delay(2000);
            IsTypesSaved = false;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
            MessageBox.Show(ex.ToString(), LocalizationService.GetString("Mapping_SaveError"));
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        var item = new MappingRuleItem(_dialogService, AvailableFamilyNames);
        MappingRules.Add(item);
        SelectedRule = item;
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule is not null)
            MappingRules.Remove(SelectedRule);
    }

    [RelayCommand]
    private async Task SaveRules()
    {
        try
        {
            _repository.SaveMappingRules(MappingRules.Select(r => r.ToRule()).ToList());
            _repository.SaveConnectorTypes(ConnectorTypes.Select(t => t.ToDefinition()).ToList());
            IsRulesSaved = true;
            await Task.Delay(2000);
            IsRulesSaved = false;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
            MessageBox.Show(ex.ToString(), LocalizationService.GetString("Mapping_SaveError"));
        }
    }

    // ── Import / Export (ADR-012) ─────────────────────────────────────────

    /// <summary>
    /// Path of the legacy AppData JSON file. Used as the default location of the
    /// Import dialog so users migrating from pre-ADR-012 versions can locate their
    /// data without browsing <c>%APPDATA%</c> manually.
    /// </summary>
    private static readonly string LegacyAppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon");

    private const string LegacyAppDataFileName = "connector-mapping.json";

    [RelayCommand]
    private void ImportFromFile()
    {
        try
        {
            string? initialDirectory = Directory.Exists(LegacyAppDataDirectory) ? LegacyAppDataDirectory : null;
            string? preselect = initialDirectory is not null
                && File.Exists(Path.Combine(initialDirectory, LegacyAppDataFileName))
                    ? LegacyAppDataFileName
                    : null;

            var filePath = _dialogService.ShowOpenJsonDialog(
                LocalizationService.GetString("Mapping_ImportTitle"),
                initialDirectory,
                preselect);

            if (filePath is null) return;

            var payload = FittingMappingJsonSerializer.TryReadFromFile(filePath);
            if (payload is null)
            {
                _dialogService.ShowWarning(
                    LocalizationService.GetString("Mapping_ImportErrorTitle"),
                    LocalizationService.GetString("Mapping_ImportFailed"));
                return;
            }

            ConnectorTypes.Clear();
            foreach (var t in payload.ConnectorTypes)
                ConnectorTypes.Add(ConnectorTypeItem.From(t));

            MappingRules.Clear();
            foreach (var r in payload.MappingRules)
                MappingRules.Add(MappingRuleItem.From(r, _dialogService, AvailableFamilyNames));

            _repository.SaveConnectorTypes(payload.ConnectorTypes);
            _repository.SaveMappingRules(payload.MappingRules);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
            _dialogService.ShowWarning(
                LocalizationService.GetString("Mapping_ImportErrorTitle"),
                ex.Message);
        }
    }

    [RelayCommand]
    private void ExportToFile()
    {
        try
        {
            var filePath = _dialogService.ShowSaveJsonDialog(
                LocalizationService.GetString("Mapping_ExportTitle"),
                LocalizationService.GetString("Mapping_ExportDefaultFileName"));

            if (filePath is null) return;

            var payload = new MappingPayload(
                FittingMappingJsonSerializer.CurrentVersion,
                ConnectorTypes.Select(t => t.ToDefinition()).ToList(),
                MappingRules.Select(r => r.ToRule()).ToList());

            FittingMappingJsonSerializer.WriteToFile(filePath, payload);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
            MessageBox.Show(ex.ToString(), LocalizationService.GetString("Mapping_SaveError"));
        }
    }
}
