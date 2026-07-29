using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class AttributeListItemViewModel : ObservableObject
{
    [ObservableProperty] private string _attributeId = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _group;
    [ObservableProperty] private bool _isBound;
    [ObservableProperty] private bool _isInherited;
    [ObservableProperty] private string? _sourceCategoryName;
    [ObservableProperty] private string? _bindingId;
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>
    /// Import Validation Gate: number of validation rules attached to
    /// this attribute's binding (direct or inherited-from-parent binding).
    /// Drives the shield badge in the category editor.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRules))]
    [NotifyPropertyChangedFor(nameof(RulesTooltip))]
    private int _ruleCount;

    public bool HasRules => RuleCount > 0;

    /// <summary>Rules can be edited only for a persisted binding
    /// (a draft category/binding has no row to attach rules to).</summary>
    public bool CanEditRules => BindingId is not null;

    public string RulesTooltip => HasRules
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_CTE_RulesCount) ?? "Validation rules: {0}",
            RuleCount)
        : string.Empty;

    public bool OriginalIsBound { get; set; }
    public bool IsDirty { get; set; }

    public CategoryTreeEditorViewModel? Parent { get; set; }

    [RelayCommand]
    private void ToggleBinding()
    {
        Parent?.HandleBindingToggle(this, !IsBound);
    }

    [RelayCommand]
    private async Task OpenValidationRules()
    {
        if (Parent is not null)
        {
            await Parent.OpenValidationRulesEditorAsync(this);
        }
    }
}
