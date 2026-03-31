using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Редактируемая обёртка FittingMappingRule для DataGrid в MappingEditorView.
/// Список семейств управляется через FamilySelectorView (кнопка "Выбрать...").
/// </summary>
public sealed partial class MappingRuleItem : ObservableObject
{
    private readonly IDialogService _dialogService;
    private readonly IReadOnlyList<string> _availableFamilyNames;

    [ObservableProperty]
    private int _fromTypeCode;

    [ObservableProperty]
    private int _toTypeCode;

    [ObservableProperty]
    private bool _isDirectConnect;

    public ObservableCollection<FittingMapping> FittingFamilies { get; } = [];

    /// <summary>Суммарное отображение выбранных семейств для DataGrid.</summary>
    public string FamiliesSummary =>
        FittingFamilies.Count > 0
            ? string.Join(", ", FittingFamilies.Select(f => f.FamilyName))
            : "(не выбраны)";

    public MappingRuleItem(IDialogService dialogService, IReadOnlyList<string> availableFamilyNames)
    {
        _dialogService = dialogService;
        _availableFamilyNames = availableFamilyNames;
        FittingFamilies.CollectionChanged += OnFamiliesChanged;
    }

    private void OnFamiliesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(FamiliesSummary));

    public static MappingRuleItem From(
        FittingMappingRule r,
        IDialogService dialogService,
        IReadOnlyList<string> availableFamilyNames)
    {
        var item = new MappingRuleItem(dialogService, availableFamilyNames)
        {
            FromTypeCode = r.FromType.Value,
            ToTypeCode = r.ToType.Value,
            IsDirectConnect = r.IsDirectConnect,
        };
        foreach (var f in r.FittingFamilies.OrderBy(f => f.Priority))
            item.FittingFamilies.Add(f);
        return item;
    }

    [RelayCommand]
    private void OpenFamilySelector()
    {
        var result = _dialogService.ShowFamilySelector(
            _availableFamilyNames,
            FittingFamilies.ToList());

        if (result is null) return;

        FittingFamilies.Clear();
        foreach (var f in result)
            FittingFamilies.Add(f);
    }

    public FittingMappingRule ToRule() => new()
    {
        FromType = new ConnectionTypeCode(FromTypeCode),
        ToType = new ConnectionTypeCode(ToTypeCode),
        IsDirectConnect = IsDirectConnect,
        FittingFamilies = FittingFamilies.ToList(),
    };
}
