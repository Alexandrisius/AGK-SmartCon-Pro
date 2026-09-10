using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Одна строка-условие расширенного поиска (#87): источник (атрибут
/// библиотеки / системное поле) + оператор + значение. Механика переключения
/// источника повторяет редактор правил автоназначения (#241): смена
/// источника сбрасывает значение и пересчитывает список допустимых
/// операторов. Живёт внутри <see cref="AdvancedSearchViewModel"/>.
/// </summary>
public sealed partial class AdvancedSearchConditionViewModel : ObservableObject
{
    private readonly AdvancedSearchViewModel _owner;
    private string? _selectedAttributeName;
    private bool _restoring;

    [ObservableProperty]
    private AssignmentConditionSourceKind _sourceKind = AssignmentConditionSourceKind.Attribute;

    [ObservableProperty]
    private string? _selectedAttributeId;

    [ObservableProperty]
    private AssignmentSystemField? _systemField;

    [ObservableProperty]
    private ValidationRuleOperator _operator = ValidationRuleOperator.Equals;

    [ObservableProperty]
    private string? _valueText;

    /// <summary>Distinct значения атрибута по всему каталогу (подсказки).</summary>
    public IReadOnlyList<string> ValueSuggestions { get; private set; } = [];

    /// <summary>Подсказки, отфильтрованные по введённому тексту.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _filteredSuggestions = [];

    /// <summary>Показывает/скрывает выпадающий список подсказок.</summary>
    [ObservableProperty]
    private bool _isSuggestionsOpen;

    /// <summary>Выбранная подсказка — вставляется в ValueText и закрывает список.</summary>
    [ObservableProperty]
    private string? _selectedSuggestion;

    /// <summary>Операторы, допустимые для текущего источника.</summary>
    [ObservableProperty]
    private IReadOnlyList<AdvancedSearchViewModel.AdvancedSearchOperatorItem> _availableOperators = [];

    public AdvancedSearchConditionViewModel(AdvancedSearchViewModel owner)
    {
        _owner = owner;
        _availableOperators = _owner.GetOperatorsFor(SourceKind, SystemField);
    }

    // ── Видимость редакторов значения ─────────────────────────────────

    public bool IsAttributeSource => SourceKind == AssignmentConditionSourceKind.Attribute;

    /// <summary>ComboBox-friendly прокси SourceKind (0 = атрибут, 1 = системное поле).</summary>
    public int SourceKindIndex
    {
        get => SourceKind == AssignmentConditionSourceKind.System ? 1 : 0;
        set => SourceKind = value == 1 ? AssignmentConditionSourceKind.System : AssignmentConditionSourceKind.Attribute;
    }

    /// <summary>Текстовое поле с подсказками — атрибут библиотеки с
    /// оператором, требующим значения.</summary>
    public bool ShowSuggestBox =>
        IsAttributeSource
        && OperatorRequiresValue;

    /// <summary>Простое текстовое поле без подсказок — имя семейства.</summary>
    public bool ShowTextBox =>
        !IsAttributeSource
        && SystemField == AssignmentSystemField.FamilyName
        && OperatorRequiresValue;

    public bool ShowCategoryPicker =>
        !IsAttributeSource && SystemField == AssignmentSystemField.RevitCategory;

    public bool ShowPartTypePicker =>
        !IsAttributeSource && SystemField == AssignmentSystemField.PartType;

    /// <summary>HasValue/IsEmpty значения не требуют — тихая заглушка «—»
    /// сохраняет высоту строки.</summary>
    public bool ShowNoValuePlaceholder =>
        !ShowSuggestBox && !ShowTextBox && !ShowCategoryPicker && !ShowPartTypePicker;

    private bool OperatorRequiresValue => AttributeFilterCondition.RequiresValue(Operator);

    /// <summary>Есть ли хоть одна подсказка после фильтрации — для empty-текста в Popup.</summary>
    public bool HasFilteredSuggestions => FilteredSuggestions.Count > 0;

    public bool HasNoFilteredSuggestions => !HasFilteredSuggestions;

    [RelayCommand]
    private void ToggleSuggestions() => IsSuggestionsOpen = !IsSuggestionsOpen;

    public bool IsValueMissing =>
        (ShowSuggestBox || ShowTextBox || ShowCategoryPicker || ShowPartTypePicker)
        && string.IsNullOrWhiteSpace(ValueText);

    /// <summary>
    /// Условие валидно, когда поле выбрано (атрибут — из списка атрибутов
    /// текущей категории; системное поле — любое) и значение заполнено, если
    /// оператор его требует.
    /// </summary>
    public bool IsValid =>
        IsAttributeSource
            ? SelectedAttributeId is not null
              && _owner.AvailableAttributes.Any(a => a.AttributeId == SelectedAttributeId)
              && !IsValueMissing
            : SystemField is not null && !IsValueMissing;

    public string? SelectedAttributeName => _selectedAttributeName;

    /// <summary>
    /// Восстанавливает сохранённое условие при открытии диалога. Имя атрибута
    /// сохраняется отдельно: список атрибутов подгружается асинхронно и на
    /// момент восстановления может ещё не содержать искомый элемент.
    /// Пишет через свойства с <c>_restoring</c>-гвардом: прямая запись в
    /// поля [ObservableProperty] запрещена анализатором (MVVMTK0034), а
    /// каскад property-changed (сброс значения, пересчёт операторов) при
    /// восстановлении не нужен.
    /// </summary>
    public void Restore(
        AssignmentConditionSourceKind sourceKind,
        string? attributeId,
        string? attributeName,
        AssignmentSystemField? systemField,
        ValidationRuleOperator op,
        string? value)
    {
        _restoring = true;
        try
        {
            SourceKind = sourceKind;
            _selectedAttributeName = attributeName;
            SelectedAttributeId = attributeId;
            SystemField = systemField;
            Operator = op;
            ValueText = value;
        }
        finally
        {
            _restoring = false;
        }

        OnPropertyChanged(nameof(IsAttributeSource));
        OnPropertyChanged(nameof(SourceKindIndex));
        AvailableOperators = _owner.GetOperatorsFor(SourceKind, SystemField);
        RefreshValueEditors();
        OnPropertyChanged(nameof(IsValid));
        _owner.NotifyConditionChanged();

        if (IsAttributeSource && SelectedAttributeId is not null)
        {
            _ = LoadValueSuggestionsAsync();
        }
    }

    partial void OnSourceKindChanged(AssignmentConditionSourceKind value)
    {
        if (_restoring)
        {
            return;
        }

        // Пара источник-поле всегда консистентна с UI (инцидент #248):
        // переключение на системное поле предвыбирает первое, обратно —
        // полностью очищает SystemField.
        if (value == AssignmentConditionSourceKind.System)
        {
            if (SystemField is null)
            {
                SystemField = AssignmentSystemField.RevitCategory;
                // OnSystemFieldChanged уже сбросил значение и пересчитал операторы.
                OnPropertyChanged(nameof(IsAttributeSource));
                OnPropertyChanged(nameof(SourceKindIndex));
                return;
            }
        }
        else
        {
            SystemField = null;
        }

        ValueText = null;
        OnPropertyChanged(nameof(IsAttributeSource));
        OnPropertyChanged(nameof(SourceKindIndex));
        RecomputeOperators();
    }

    partial void OnSystemFieldChanged(AssignmentSystemField? value)
    {
        if (_restoring)
        {
            return;
        }

        if (SourceKind == AssignmentConditionSourceKind.System)
        {
            // Сохранённое значение не переносимо между полями (ordinal
            // категории Revit — не тип детали и не имя) — сбрасываем.
            ValueText = null;
            RecomputeOperators();
        }
    }

    partial void OnSelectedAttributeIdChanged(string? value)
    {
        if (_restoring)
        {
            return;
        }

        var known = _owner.AvailableAttributes.FirstOrDefault(a => a.AttributeId == value);
        if (known is not null)
        {
            _selectedAttributeName = known.Name;
        }

        ValueText = null;
        OnPropertyChanged(nameof(IsValid));
        _owner.NotifyConditionChanged();
        _ = LoadValueSuggestionsAsync();
    }

    partial void OnOperatorChanged(ValidationRuleOperator value)
    {
        if (_restoring)
        {
            return;
        }

        IsSuggestionsOpen = false;
        RefreshValueEditors();
        OnPropertyChanged(nameof(IsValid));
        _owner.NotifyConditionChanged();
    }

    partial void OnValueTextChanged(string? value)
    {
        if (_restoring)
        {
            return;
        }

        RefreshFilteredSuggestions();
        OnPropertyChanged(nameof(IsValueMissing));
        OnPropertyChanged(nameof(IsValid));
        _owner.NotifyConditionChanged();
    }

    partial void OnFilteredSuggestionsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasFilteredSuggestions));
        OnPropertyChanged(nameof(HasNoFilteredSuggestions));
    }

    partial void OnSelectedSuggestionChanged(string? value)
    {
        if (value is null)
        {
            return;
        }

        ValueText = value;
        IsSuggestionsOpen = false;
        // Сброс, чтобы повторный выбор той же подсказки снова сработал.
        SelectedSuggestion = null;
    }

    /// <summary>Вызывается владельцем после загрузки списка атрибутов —
    /// валидность условий зависит от состава списка.</summary>
    internal void OnAvailableAttributesChanged()
    {
        OnPropertyChanged(nameof(IsValid));
    }

    private void RecomputeOperators()
    {
        AvailableOperators = _owner.GetOperatorsFor(SourceKind, SystemField);
        if (AvailableOperators.Count > 0 && AvailableOperators.All(o => o.Operator != Operator))
        {
            Operator = AvailableOperators[0].Operator;
        }

        RefreshValueEditors();
    }

    private void RefreshValueEditors()
    {
        OnPropertyChanged(nameof(ShowSuggestBox));
        OnPropertyChanged(nameof(ShowTextBox));
        OnPropertyChanged(nameof(ShowCategoryPicker));
        OnPropertyChanged(nameof(ShowPartTypePicker));
        OnPropertyChanged(nameof(ShowNoValuePlaceholder));
        OnPropertyChanged(nameof(IsValueMissing));
    }

    private void RefreshFilteredSuggestions()
    {
        var query = (ValueText ?? string.Empty).Trim();
        FilteredSuggestions = string.IsNullOrEmpty(query)
            ? ValueSuggestions
            : ValueSuggestions
                .Where(s => ContainsIgnoreCase(s, query))
                .ToList();
    }

    /// <summary>Contains с StringComparison: перегрузка string.Contains(string, StringComparison)
    /// отсутствует на net48 (R19-R24) — тот же паттерн, что в ComboBoxFilterBehavior.</summary>
    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
#if NETFRAMEWORK
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#else
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#endif
    }

    private async Task LoadValueSuggestionsAsync()
    {
        try
        {
            var attributeId = SelectedAttributeId;
            if (!IsAttributeSource || attributeId is null)
            {
                ValueSuggestions = [];
                RefreshFilteredSuggestions();
                return;
            }

            var attributeName = _selectedAttributeName ?? string.Empty;
            ValueSuggestions = await _owner.GetSuggestionsAsync(attributeId, attributeName) ?? [];
            RefreshFilteredSuggestions();
        }
        catch (Exception ex)
        {
            // Подсказки — вспомогательный UI: сбой загрузки не рушит диалог.
            SmartCon.Core.Logging.SmartConLogger.Debug(
                $"AdvancedSearch.LoadSuggestions failed: {ex.Message}");
            ValueSuggestions = [];
            RefreshFilteredSuggestions();
        }
    }
}
