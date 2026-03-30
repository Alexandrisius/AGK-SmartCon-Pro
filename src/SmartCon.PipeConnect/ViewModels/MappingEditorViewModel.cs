using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// ViewModel немодального окна управления типами коннекторов и правилами маппинга (3B).
/// </summary>
public sealed partial class MappingEditorViewModel : ObservableObject
{
    private readonly IFittingMappingRepository _repository;

    public ObservableCollection<ConnectorTypeItem> ConnectorTypes { get; } = [];
    public ObservableCollection<MappingRuleItem> MappingRules { get; } = [];

    /// <summary>Список семейств, загруженных из текущего Revit-проекта.</summary>
    public IReadOnlyList<string> AvailableFamilyNames { get; }

    [ObservableProperty]
    private ConnectorTypeItem? _selectedType;

    [ObservableProperty]
    private MappingRuleItem? _selectedRule;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public MappingEditorViewModel(
        IFittingMappingRepository repository,
        IReadOnlyList<string> availableFamilyNames)
    {
        _repository = repository;
        AvailableFamilyNames = availableFamilyNames;

        foreach (var t in _repository.GetConnectorTypes())
            ConnectorTypes.Add(ConnectorTypeItem.From(t));

        foreach (var r in _repository.GetMappingRules())
            MappingRules.Add(MappingRuleItem.From(r));
    }

    [RelayCommand]
    private void AddType()
    {
        var next = ConnectorTypes.Count > 0 ? ConnectorTypes.Max(t => t.Code) + 1 : 1;
        var item = new ConnectorTypeItem { Code = next, Name = "Новый тип" };
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
    private void SaveTypes()
    {
        try
        {
            _repository.SaveConnectorTypes(ConnectorTypes.Select(t => t.ToDefinition()).ToList());
            StatusMessage = $"✔ Типы сохранены ({ConnectorTypes.Count} шт.) — {_repository.GetStoragePath()}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"✘ Ошибка: {ex.Message}";
            MessageBox.Show(ex.ToString(), "SmartCon — ошибка сохранения");
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        var item = new MappingRuleItem();
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
    private void SaveRules()
    {
        try
        {
            _repository.SaveMappingRules(MappingRules.Select(r => r.ToRule()).ToList());
            StatusMessage = $"✔ Правила сохранены ({MappingRules.Count} шт.) — {_repository.GetStoragePath()}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"✘ Ошибка: {ex.Message}";
            MessageBox.Show(ex.ToString(), "SmartCon — ошибка сохранения");
        }
    }
}
