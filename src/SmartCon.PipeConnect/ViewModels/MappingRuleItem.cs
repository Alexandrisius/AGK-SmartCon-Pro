using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Редактируемая обёртка FittingMappingRule для DataGrid в MappingEditorView.
/// FittingFamilies хранятся как CSV (FamilyName через запятую) для простоты редактирования.
/// </summary>
public sealed partial class MappingRuleItem : ObservableObject
{
    [ObservableProperty]
    private int _fromTypeCode;

    [ObservableProperty]
    private int _toTypeCode;

    [ObservableProperty]
    private bool _isDirectConnect;

    [ObservableProperty]
    private string _fittingFamiliesCsv = string.Empty;

    public static MappingRuleItem From(FittingMappingRule r) =>
        new()
        {
            FromTypeCode = r.FromType.Value,
            ToTypeCode = r.ToType.Value,
            IsDirectConnect = r.IsDirectConnect,
            FittingFamiliesCsv = string.Join(", ", r.FittingFamilies.Select(f => f.FamilyName)),
        };

    public FittingMappingRule ToRule()
    {
        var families = FittingFamiliesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((name, i) => new FittingMapping { FamilyName = name, Priority = i + 1 })
            .ToList();

        return new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(FromTypeCode),
            ToType = new ConnectionTypeCode(ToTypeCode),
            IsDirectConnect = IsDirectConnect,
            FittingFamilies = families,
        };
    }
}
