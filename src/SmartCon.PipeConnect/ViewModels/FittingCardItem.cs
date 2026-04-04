using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// Элемент выпадающего списка фитингов в PipeConnectEditorView.
/// Один элемент = одно конкретное семейство (FittingMapping) из правила маппинга.
/// </summary>
public sealed partial class FittingCardItem : ObservableObject
{
    public FittingMappingRule Rule { get; }

    /// <summary>Отображаемое имя в ComboBox.</summary>
    public string DisplayName { get; }

    /// <summary>Конкретное семейство фитинга, null = прямое соединение.</summary>
    public FittingMapping? PrimaryFitting { get; }

    public bool IsDirectConnect => Rule.IsDirectConnect && PrimaryFitting is null;

    public override string ToString() => DisplayName;

    public FittingCardItem(FittingMappingRule rule, FittingMapping? fitting = null)
    {
        Rule = rule;
        PrimaryFitting = fitting;
        DisplayName = BuildDisplayName(rule, fitting);
    }

    private static string BuildDisplayName(FittingMappingRule rule, FittingMapping? fitting)
    {
        if (rule.IsDirectConnect && fitting is null)
            return "Без фитинга (прямое соединение)";

        if (fitting is not null)
            return fitting.SymbolName != "*"
                ? $"{fitting.FamilyName} — {fitting.SymbolName}"
                : fitting.FamilyName;

        return $"Тип {rule.FromType.Value} → {rule.ToType.Value}";
    }
}
