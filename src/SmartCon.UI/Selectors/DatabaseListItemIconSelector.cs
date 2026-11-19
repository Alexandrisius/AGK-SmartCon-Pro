using System.Windows;
using System.Windows.Controls;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.UI.Selectors;

/// <summary>
/// Picks the <see cref="DataTemplate"/> for a <see cref="DatabaseListItem"/>
/// according to its <see cref="DatabaseListItem.MatchStatus"/> so the
/// ComboBox shows the right Path icon (general / project-match / project-mismatch).
/// See #119 — three variants, no font dependency.
/// </summary>
public sealed class DatabaseListItemIconSelector : DataTemplateSelector
{
    public DataTemplate? GeneralTemplate { get; set; }
    public DataTemplate? ProjectMatchTemplate { get; set; }
    public DataTemplate? ProjectMismatchTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not DatabaseListItem dli) return base.SelectTemplate(item, container);

        if (dli.Kind == BaseType.General) return GeneralTemplate;
        return dli.MatchStatus switch
        {
            ProjectBaseMatchKind.Match => ProjectMatchTemplate,
            ProjectBaseMatchKind.Mismatch => ProjectMismatchTemplate,
            _ => GeneralTemplate
        };
    }
}