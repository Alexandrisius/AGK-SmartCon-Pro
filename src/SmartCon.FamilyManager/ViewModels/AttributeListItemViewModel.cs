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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditRules))]
    private string? _bindingId;
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>
    /// Import Validation Gate: number of validation rules attached to
    /// this attribute's binding (direct or inherited-from-parent binding).
    /// Drives the shield badge in the category editor.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRules))]
    [NotifyPropertyChangedFor(nameof(ShieldIconKind))]
    [NotifyPropertyChangedFor(nameof(ShieldBrush))]
    [NotifyPropertyChangedFor(nameof(RulesTooltip))]
    private int _ruleCount;

    /// <summary>
    /// Import Validation Gate: how many of the binding's rules are
    /// disabled. Drives the orange shield-alert state so a disabled rule
    /// stays visible instead of silently stopping to protect the category.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShieldIconKind))]
    [NotifyPropertyChangedFor(nameof(ShieldBrush))]
    [NotifyPropertyChangedFor(nameof(RulesTooltip))]
    private int _disabledRuleCount;

    public bool HasRules => RuleCount > 0;

    /// <summary>Rules can be edited only for a persisted binding
    /// (a draft category/binding has no row to attach rules to).</summary>
    public bool CanEditRules => BindingId is not null;

    /// <summary>
    /// Shield button icon: outline shield when no rules, green shield-check
    /// when every rule is enabled, orange shield-alert when at least one
    /// rule is disabled.
    /// </summary>
    public string ShieldIconKind =>
        !HasRules ? "ShieldOutline"
        : DisabledRuleCount > 0 ? "ShieldAlertOutline"
        : "ShieldCheck";

    public string ShieldBrush =>
        !HasRules ? "#9E9E9E"
        : DisabledRuleCount > 0 ? "#FB8C00"
        : "#4CAF50";

    public string RulesTooltip
    {
        get
        {
            static string? Loc(string key) => SmartCon.UI.LanguageManager.GetString(key);
            if (!HasRules)
            {
                return Loc(SmartCon.UI.StringLocalization.Keys.FM_CTE_RulesNone)
                    ?? "Правила валидации не заданы — нажмите для настройки";
            }

            if (DisabledRuleCount > 0)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_CTE_RulesCountDisabled)
                        ?? "Правила валидации: {0} (отключено: {1})",
                    RuleCount,
                    DisabledRuleCount);
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_CTE_RulesCount) ?? "Правила валидации: {0}",
                RuleCount);
        }
    }

    /// <summary>
    /// Re-pushes <see cref="IsBound"/> to the OneWay-bound CheckBox —
    /// needed when the parent cancels a toggle (e.g. the user declines
    /// the "rules will be deleted" unbind confirmation) so the CheckBox
    /// visually snaps back to the unchanged state.
    /// </summary>
    public void NotifyBoundStateChanged() => OnPropertyChanged(nameof(IsBound));

    public CategoryTreeEditorViewModel? Parent { get; set; }

    [RelayCommand]
    private async Task ToggleBinding()
    {
        if (Parent is not null)
        {
            await Parent.HandleBindingToggleAsync(this, !IsBound);
        }
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
