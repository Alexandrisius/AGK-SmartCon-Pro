using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Editor for the validation rules of ONE category-attribute binding.
/// Opened from the category tree editor's attribute context menu
/// ("Правила валидации"). Saves directly to the catalog database via
/// <see cref="IValidationRuleRepository"/> (rules live and die with
/// their binding — FK CASCADE).
/// </summary>
public sealed partial class ValidationRulesEditorViewModel : ObservableObject, IObservableRequestClose
{
    public event Action<bool?>? RequestClose;

    private readonly string _bindingId;
    private readonly IValidationRuleRepository _ruleRepository;

    public string AttributeName { get; }
    public string CategoryPath { get; }

    [ObservableProperty]
    private ObservableCollection<ValidationRuleRowViewModel> _rules = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasRules;

    public IReadOnlyList<ValidationRuleOperatorItem> AvailableOperators { get; }

    public ValidationRulesEditorViewModel(
        string bindingId,
        string attributeName,
        string categoryPath,
        IValidationRuleRepository ruleRepository)
    {
        _bindingId = bindingId;
        _ruleRepository = ruleRepository;
        AttributeName = attributeName;
        CategoryPath = categoryPath;
        AvailableOperators = BuildOperatorItems();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var rules = await _ruleRepository.GetRulesForBindingAsync(_bindingId, ct);
        Rules = new ObservableCollection<ValidationRuleRowViewModel>(
            rules.Select(r => new ValidationRuleRowViewModel(
                r.Id,
                r.Operator,
                r.ValueText ?? r.ValueNumber?.ToString("G15", CultureInfo.InvariantCulture),
                r.MinValue?.ToString("G15", CultureInfo.InvariantCulture),
                r.MaxValue?.ToString("G15", CultureInfo.InvariantCulture),
                r.IsEnabled,
                r.SortOrder)));
        HasRules = Rules.Count > 0;
    }

    [RelayCommand]
    private void AddRule()
    {
        Rules.Add(new ValidationRuleRowViewModel(
            null, ValidationRuleOperator.HasValue, null, null, null, true, Rules.Count));
        HasRules = true;
    }

    [RelayCommand]
    private void DeleteRule(ValidationRuleRowViewModel? row)
    {
        if (row is null) return;
        Rules.Remove(row);
        HasRules = Rules.Count > 0;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        using var _scope = SmartConLogger.BeginScope("ValidationRules",
            ("Method", nameof(SaveAsync)),
            ("BindingId", _bindingId));

        if (!ValidateRows(out var error))
        {
            StatusMessage = error!;
            return;
        }

        try
        {
            var existing = await _ruleRepository.GetRulesForBindingAsync(_bindingId);
            var existingById = existing.ToDictionary(r => r.Id);
            var presentIds = Rules.Where(r => r.Id is not null).Select(r => r.Id!).ToHashSet();

            foreach (var removed in existing.Where(r => !presentIds.Contains(r.Id)))
            {
                await _ruleRepository.DeleteRuleAsync(removed.Id);
            }

            var sortOrder = 0;
            foreach (var row in Rules)
            {
                var rule = MapRow(row, sortOrder++);
                if (row.Id is null)
                {
                    await _ruleRepository.CreateRuleAsync(rule);
                }
                else
                {
                    await _ruleRepository.UpdateRuleAsync(rule);
                }
            }

            SmartConLogger.Info($"Saved {Rules.Count} validation rule(s) for binding {_bindingId}");
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed to save validation rules: {ex.Message}");
            StatusMessage = string.Format(
                SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    private bool ValidateRows(out string? error)
    {
        foreach (var row in Rules)
        {
            if (row.ShowValueField && string.IsNullOrWhiteSpace(row.Value))
            {
                error = Localize("FM_RulesEditor_ErrorValueRequired", "Value is required for this operator");
                return false;
            }

            if (row.ShowNumberField && !IsNumber(row.Value))
            {
                error = Localize("FM_RulesEditor_ErrorNumberRequired", "A number is required for this operator");
                return false;
            }

            if (row.ShowRangeFields)
            {
                if (!IsNumber(row.MinValue) || !IsNumber(row.MaxValue))
                {
                    error = Localize("FM_RulesEditor_ErrorRangeRequired", "Both range bounds must be numbers");
                    return false;
                }

                if (ParseNumber(row.MinValue)!.Value > ParseNumber(row.MaxValue)!.Value)
                {
                    error = Localize("FM_RulesEditor_ErrorRangeOrder", "The lower bound must not exceed the upper bound");
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private ValidationRule MapRow(ValidationRuleRowViewModel row, int sortOrder)
    {
        string? valueText = null;
        double? valueNumber = null;
        double? minValue = null;
        double? maxValue = null;

        if (row.ShowValueField)
        {
            // Equals/NotEquals auto-detect number vs text; Contains is always text.
            if (row.Operator is ValidationRuleOperator.Equals or ValidationRuleOperator.NotEquals
                && IsNumber(row.Value))
            {
                valueNumber = ParseNumber(row.Value);
            }
            else
            {
                valueText = row.Value;
            }
        }
        else if (row.ShowNumberField)
        {
            valueNumber = ParseNumber(row.Value);
        }
        else if (row.ShowRangeFields)
        {
            minValue = ParseNumber(row.MinValue);
            maxValue = ParseNumber(row.MaxValue);
        }

        return new ValidationRule(
            row.Id ?? string.Empty,
            _bindingId,
            row.Operator,
            valueText,
            valueNumber,
            minValue,
            maxValue,
            UnitTypeId: null,
            sortOrder,
            row.IsEnabled);
    }

    private static bool IsNumber(string? text) => ParseNumber(text) is not null;

    private static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant))
            return invariant;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var current))
            return current;
        return null;
    }

    private static string Localize(string key, string fallback) =>
        SmartCon.UI.LanguageManager.GetString(key) ?? fallback;

    private static IReadOnlyList<ValidationRuleOperatorItem> BuildOperatorItems()
    {
        string Loc(string k, string f) => Localize(k, f);
        return
        [
            new(ValidationRuleOperator.HasValue, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_HasValue, "Has value")),
            new(ValidationRuleOperator.IsPresent, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_IsPresent, "Parameter exists")),
            new(ValidationRuleOperator.IsEmpty, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_IsEmpty, "Is empty")),
            new(ValidationRuleOperator.Equals, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_Equals, "Equals")),
            new(ValidationRuleOperator.NotEquals, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_NotEquals, "Not equals")),
            new(ValidationRuleOperator.Contains, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_Contains, "Contains")),
            new(ValidationRuleOperator.NotContains, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_NotContains, "Not contains")),
            new(ValidationRuleOperator.GreaterThan, ">"),
            new(ValidationRuleOperator.GreaterOrEqual, "≥"),
            new(ValidationRuleOperator.LessThan, "<"),
            new(ValidationRuleOperator.LessOrEqual, "≤"),
            new(ValidationRuleOperator.Between, Loc(SmartCon.UI.StringLocalization.Keys.FM_RuleOp_Between, "Between")),
        ];
    }
}

public sealed record ValidationRuleOperatorItem(ValidationRuleOperator Operator, string DisplayName);
