using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Элемент выпадающего списка фитингов в PipeConnectEditorView.
/// Один элемент = одно правило маппинга (FittingMappingRule).
/// </summary>
public sealed partial class FittingCardItem : ObservableObject
{
    public FittingMappingRule Rule { get; }

    /// <summary>Отображаемое имя в ComboBox.</summary>
    public string DisplayName { get; }

    /// <summary>Первый (приоритетный) вариант семейства, null = прямое соединение.</summary>
    public FittingMapping? PrimaryFitting { get; }

    public bool IsDirectConnect => Rule.IsDirectConnect && Rule.FittingFamilies.Count == 0;

    public override string ToString() => DisplayName;

    public FittingCardItem(FittingMappingRule rule)
    {
        Rule = rule;
        PrimaryFitting = rule.FittingFamilies
            .OrderBy(f => f.Priority)
            .FirstOrDefault();

        DisplayName = BuildDisplayName(rule);
    }

    private static string BuildDisplayName(FittingMappingRule rule)
    {
        if (rule.IsDirectConnect && rule.FittingFamilies.Count == 0)
            return "Без фитинга (прямое соединение)";

        var primary = rule.FittingFamilies.OrderBy(f => f.Priority).FirstOrDefault();
        if (primary is not null)
            return primary.SymbolName != "*"
                ? $"{primary.FamilyName} — {primary.SymbolName}"
                : primary.FamilyName;

        return $"Тип {rule.FromType.Value} → {rule.ToType.Value}";
    }
}
