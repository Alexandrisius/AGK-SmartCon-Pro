using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ExportNameDialogViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private string _currentFileName = string.Empty;

    [ObservableProperty]
    private string _validationErrors = string.Empty;

    [ObservableProperty]
    private string _previewFileName = string.Empty;

    [ObservableProperty]
    private bool _isValid;

    [ObservableProperty]
    private string _fieldErrors = string.Empty;

    public ObservableCollection<ExportNameFieldItem> Fields { get; } = [];

    private readonly List<FieldDefinition> _fieldLibrary;
    private readonly List<ExportMapping> _exportMappings;

    public event Action<bool?>? RequestClose;

    public ExportNameDialogViewModel(
        string currentFileName,
        string validationErrors,
        List<FileBlockDefinition> blocks,
        List<FieldDefinition> fieldLibrary,
        List<ExportMapping> exportMappings)
    {
        _currentFileName = currentFileName;
        _validationErrors = validationErrors;
        _fieldLibrary = fieldLibrary;
        _exportMappings = exportMappings;

        var parser = ServiceHost.GetService<IFileNameParser>();
        var template = new FileNameTemplate { Blocks = blocks, ExportMappings = exportMappings };

        var parsed = parser.ParseBlocks(currentFileName, new FileNameTemplate { Blocks = blocks });

        var transformed = parser.TransformForExport(currentFileName, template, fieldLibrary);
        var transformedParsed = parser.ParseBlocks(transformed ?? currentFileName, new FileNameTemplate { Blocks = blocks });

        foreach (var block in blocks.OrderBy(b => b.Index))
        {
            var parsedValue = parsed.TryGetValue(block.Field, out var v) ? v : string.Empty;
            var mappedValue = transformedParsed.TryGetValue(block.Field, out var mv) ? mv : parsedValue;

            var fieldDef = fieldLibrary.FirstOrDefault(fd =>
                string.Equals(fd.Name, block.Field, StringComparison.OrdinalIgnoreCase));

            var item = new ExportNameFieldItem
            {
                Field = block.Field,
                Value = mappedValue,
                AllowedValues = fieldDef?.AllowedValues.ToList() ?? [],
                HasAllowedValues = fieldDef is not null && fieldDef.AllowedValues.Count > 0,
                MinLength = fieldDef?.MinLength,
                MaxLength = fieldDef?.MaxLength
            };
            item.PropertyChanged += OnFieldChanged;
            Fields.Add(item);
        }

        RefreshPreview();
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportNameFieldItem.Value))
            RefreshPreview();
    }

    private void RefreshPreview()
    {
        var values = Fields.Select(f => f.Value).ToList();
        PreviewFileName = string.Join("-", values);

        var errors = new StringBuilder();
        var allValid = true;

        foreach (var field in Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Value))
            {
                errors.AppendLine($"• {field.Field}: значение не заполнено");
                allValid = false;
                continue;
            }

            var fieldDef = _fieldLibrary.FirstOrDefault(fd =>
                string.Equals(fd.Name, field.Field, StringComparison.OrdinalIgnoreCase));

            if (fieldDef is null) continue;

            var mode = fieldDef.ValidationMode;

            if (mode is ValidationMode.AllowedValues or ValidationMode.AllowedValuesAndCharCount)
            {
                if (fieldDef.AllowedValues.Count > 0)
                {
                    var found = fieldDef.AllowedValues.Any(av =>
                        string.Equals(av, field.Value, StringComparison.OrdinalIgnoreCase));
                    if (!found)
                    {
                        errors.AppendLine($"• {field.Field}: значение '{field.Value}' не в списке допустимых ({string.Join(", ", fieldDef.AllowedValues)})");
                        allValid = false;
                    }
                }
            }

            if (mode is ValidationMode.CharCount or ValidationMode.AllowedValuesAndCharCount)
            {
                if (fieldDef.MinLength.HasValue && field.Value.Length < fieldDef.MinLength.Value)
                {
                    errors.AppendLine($"• {field.Field}: мин. длина {fieldDef.MinLength.Value}, введено {field.Value.Length}");
                    allValid = false;
                }
                if (fieldDef.MaxLength.HasValue && field.Value.Length > fieldDef.MaxLength.Value)
                {
                    errors.AppendLine($"• {field.Field}: макс. длина {fieldDef.MaxLength.Value}, введено {field.Value.Length}");
                    allValid = false;
                }
            }
        }

        FieldErrors = errors.ToString().TrimEnd();
        IsValid = allValid;
    }

    public Dictionary<string, string> GetFieldValues()
    {
        return Fields.ToDictionary(f => f.Field, f => f.Value);
    }

    [RelayCommand]
    private void Export()
    {
        if (!IsValid) return;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}

public sealed partial class ExportNameFieldItem : ObservableObject
{
    [ObservableProperty]
    private string _field = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private List<string> _allowedValues = [];

    [ObservableProperty]
    private bool _hasAllowedValues;

    [ObservableProperty]
    private int? _minLength;

    [ObservableProperty]
    private int? _maxLength;
}
