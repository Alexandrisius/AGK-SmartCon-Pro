using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Редактируемая обёртка FittingMappingRule для DataGrid в MappingEditorView.
/// Поддерживает открытие FamilySelector для управления списком семейств с приоритетами.
/// </summary>
public sealed partial class MappingRuleItem : ObservableObject
{
    [ObservableProperty]
    private int _fromTypeCode;

    [ObservableProperty]
    private int _toTypeCode;

    [ObservableProperty]
    private bool _isDirectConnect;

    /// <summary>Список семейств с приоритетами (1, 2, 3...).</summary>
    public ObservableCollection<FittingMapping> FittingFamilies { get; } = [];

    /// <summary>Для отображения в DataGrid: "1. СварнойШов, 2. Отвод..."</summary>
    public string FamiliesDisplayText =>
        FittingFamilies.Count == 0
            ? "(не задано)"
            : string.Join(", ", FittingFamilies.OrderBy(f => f.Priority)
                .Select(f => $"{f.Priority}.{f.FamilyName}"));

    public static MappingRuleItem From(FittingMappingRule r)
    {
        var item = new MappingRuleItem
        {
            FromTypeCode = r.FromType.Value,
            ToTypeCode = r.ToType.Value,
            IsDirectConnect = r.IsDirectConnect,
        };

        foreach (var f in r.FittingFamilies.OrderBy(f => f.Priority))
        {
            item.FittingFamilies.Add(f);
        }

        return item;
    }

    public FittingMappingRule ToRule() =>
        new()
        {
            FromType = new ConnectionTypeCode(FromTypeCode),
            ToType = new ConnectionTypeCode(ToTypeCode),
            IsDirectConnect = IsDirectConnect,
            FittingFamilies = FittingFamilies.ToList()
        };
}
