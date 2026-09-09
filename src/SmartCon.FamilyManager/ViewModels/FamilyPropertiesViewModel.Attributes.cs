using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Helpers;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
    private async Task LoadAttributesDataAsync(CancellationToken ct)
    {
        try
        {
            if (CategoryId is null)
            {
                HasNoCategory = true;
                return;
            }

            var effectiveAttrs = await _bindingService.GetEffectiveAttributesAsync(CategoryId, ct);
            var allDefs = await _attributeDefRepository.GetAllAsync(ct);
            var activeAttrIds = allDefs.Where(a => a.IsActive).Select(a => a.Id).ToHashSet();
            _effectiveAttributes = effectiveAttrs.Where(a => a.IsEnabled && activeAttrIds.Contains(a.AttributeId)).ToList();

            if (_effectiveAttributes.Count == 0)
            {
                HasNoBindings = true;
                return;
            }

            var run = await _runRepository.GetLatestRunForActiveVersionAsync(_catalogItemId, ct);
            if (run is null)
            {
                HasNotImported = true;
                return;
            }

            var completedText = run.CompletedAtUtc.HasValue
                ? run.CompletedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "—";
            ImportRunInfo = $"Импорт {completedText} • Revit {run.RevitMajorVersion} • {run.TypesCount} типов";

            var types = await _typeRepository.GetTypesForItemAsync(_catalogItemId, ct);
            AvailableTypes = new ObservableCollection<FamilyTypeSelectorItem>(
                types.Select(t => new FamilyTypeSelectorItem
                {
                    TypeId = t.Id,
                    TypeName = FamilyTypeSnapshot.ResolveDisplayName(t.Name, Name)
                }));
            HasTypes = AvailableTypes.Count > 0;

            if (!HasTypes)
            {
                AvailableTypes.Add(new FamilyTypeSelectorItem { TypeId = null, TypeName = Name });
                HasTypes = true;
            }

            // Show the selector only when there is something meaningful to
            // choose or to read: 2+ types, OR a single user-created (named)
            // type. A single '<default>' type or the virtual family-name
            // entry carries no extra information — hide the selector then,
            // mirroring the 3D viewer which drops the phantom type.
            ShowTypeSelector = types.Count > 1
                || (types.Count == 1
                    && types[0].Name != Core.Models.FamilyManager.FamilyTypeSnapshot.DefaultTypeName);

            var allValues = await _valueRepository.GetValuesForItemAsync(_catalogItemId, run.VersionId, ct);
            _allValues = allValues;

            // Property loading diagnostic logs removed

            var firstTypeId = HasTypes ? AvailableTypes[0].TypeId : null;
            var typeValues = firstTypeId is not null
                ? allValues.Where(v => v.TypeId == firstTypeId).ToList()
                : allValues.Where(v => v.TypeId is null).ToList();
            var found = typeValues.Count(v => v.Status == AttributeValueStatus.Found);
            var missing = _effectiveAttributes.Count - found;
            if (missing < 0) missing = 0;
            AttributesFoundCount = found;
            AttributesMissingCount = missing;

            HasAttributeData = true;

            if (HasTypes)
            {
                SelectedType = AvailableTypes[0];
            }
            else
            {
                LoadAttributesWithoutType(typeValues);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadAttributesDataAsync failed: {ex.Message} [Action: закройте и откройте properties снова; проверьте БД каталога]");
            AttributesStatusMessage = ex.Message;
        }
    }

    private static string LocalizeStatus(AttributeValueStatus status) => status switch
    {
        AttributeValueStatus.Found => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_Found) ?? "Найдено",
        AttributeValueStatus.MissingParameter => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_MissingParameter) ?? "Параметр не найден",
        AttributeValueStatus.EmptyValue => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_EmptyValue) ?? "Пустое значение",
        AttributeValueStatus.UnsupportedStorageType => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_UnsupportedType) ?? "Неподдерживаемый тип",
        AttributeValueStatus.ReadError => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_ReadError) ?? "Ошибка чтения",
        AttributeValueStatus.NotInFamily => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_NotInFamily) ?? "Нет в семействе",
        _ => status.ToString()
    };

    private void LoadAttributesWithoutType(IReadOnlyList<ExtractedAttributeValue> typeValues)
    {
        BuildAttributeRows(typeValues);
        RebuildAttributeTabData();
    }

    partial void OnSelectedTypeChanged(FamilyTypeSelectorItem? value)
    {
        LoadTypeAttributes(value);
    }

    private void LoadTypeAttributes(FamilyTypeSelectorItem? selected)
    {
        if (selected is null || _effectiveAttributes.Count == 0)
        {
            _allAttributeRows = [];
            RebuildAttributeTabData();
            return;
        }

        var typeValues = _allValues.Where(v => v.TypeId == selected.TypeId).ToList();
        BuildAttributeRows(typeValues);
        RebuildAttributeTabData();
    }

    private void BuildAttributeRows(IReadOnlyList<ExtractedAttributeValue> typeValues)
    {
        var rows = new List<AttributeRow>();

        var extractionParamNames = _allValues
            .Where(v => v.Status != AttributeValueStatus.NotInFamily)
            .Select(v => v.ParameterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var attr in _effectiveAttributes.OrderBy(a => a.SortOrder))
        {
            var match = typeValues.FirstOrDefault(v => v.AttributeId == attr.AttributeId)
                ?? typeValues.FirstOrDefault(v => v.ParameterName == attr.Name);

            var isNotInFamily = match is null && !extractionParamNames.Contains(attr.Name);
            var status = match?.Status ?? (isNotInFamily ? AttributeValueStatus.NotInFamily : AttributeValueStatus.MissingParameter);

            rows.Add(new AttributeRow
            {
                AttributeName = attr.Name,
                Value = match?.ValueText,
                Status = LocalizeStatus(status),
                StatusDetail = match?.Message,
                IsFound = match is not null && match.Status == AttributeValueStatus.Found,
                IsInherited = attr.IsInherited,
                Group = attr.Group,
                OriginalStatus = status
            });
        }

        _allAttributeRows = rows;
    }

    private void RebuildAttributeTabData()
    {
        var allCount = _allAttributeRows.Count;
        var groups = _allAttributeRows
            .Select(a => string.IsNullOrWhiteSpace(a.Group) ? null : a.Group)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g ?? string.Empty)
            .ToList();

        var groupItems = new List<AttributeGroupRow>
        {
            new AttributeGroupRow("__all__",
                LanguageManager.GetString(StringLocalization.Keys.FM_Props_AttributesAll) ?? "Все",
                allCount)
        };

        foreach (var group in groups)
        {
            var groupName = group ?? string.Empty;
            var count = _allAttributeRows.Count(a =>
                string.Equals(a.Group ?? string.Empty, groupName, StringComparison.OrdinalIgnoreCase));
            var isNoGroup = group is null;
            var displayName = isNoGroup
                ? LanguageManager.GetString(StringLocalization.Keys.FM_Props_AttributesNoGroup) ?? "Без группы"
                : group;
            var key = isNoGroup ? "__nogroup__" : groupName;
            groupItems.Add(new AttributeGroupRow(key, displayName ?? string.Empty, count));
        }

        AttributeGroups = new ObservableCollection<AttributeGroupRow>(groupItems);
        SelectedAttributeGroup = AttributeGroups.FirstOrDefault()?.GroupName;
    }

    private void RebuildFilteredAttributes()
    {
        IEnumerable<AttributeRow> source = _allAttributeRows;

        if (!string.Equals(SelectedAttributeGroup, "__all__", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(SelectedAttributeGroup, "__nogroup__", StringComparison.OrdinalIgnoreCase))
            {
                source = source.Where(a => string.IsNullOrWhiteSpace(a.Group));
            }
            else
            {
                source = source.Where(a =>
                    string.Equals(a.Group ?? string.Empty, SelectedAttributeGroup ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            }
        }

        var sorted = source
            .OrderBy(a => a.AttributeName)
            .ToList();

        FilteredAttributes = new ObservableCollection<AttributeRow>(sorted);
        HasFilteredAttributes = FilteredAttributes.Count > 0;
    }
}
