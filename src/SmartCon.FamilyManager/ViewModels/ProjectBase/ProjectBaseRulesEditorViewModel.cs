using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels.ProjectBase;

namespace SmartCon.FamilyManager.ViewModels.ProjectBase;

public sealed partial class ProjectBaseRulesEditorViewModel : ObservableObject, IObservableRequestClose
{
    private readonly IFileNameParser _parser;
    private readonly IFamilyManagerDialogService _dialogService;

    [ObservableProperty]
    private string _currentFilePath = string.Empty;

    public string CurrentProjectName =>
        string.IsNullOrEmpty(CurrentFilePath)
            ? LocalizationService.GetString("FM_PBase_UnsavedFilePlaceholder") ?? "(файл не сохранён)"
            : Path.GetFileNameWithoutExtension(CurrentFilePath);

    [ObservableProperty]
    private string _previewCurrent = string.Empty;

    [ObservableProperty]
    private string _previewParsed = string.Empty;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private bool _hasValidationError;

    [ObservableProperty]
    private FileNameBlockItem? _selectedBlock;

    [ObservableProperty]
    private int _selectedBlockIndex = -1;

    public ObservableCollection<FileNameBlockItem> Blocks { get; } = [];
    public ObservableCollection<FieldDefinitionItem> FieldLibrary { get; } = [];

    public event Action<bool?>? RequestClose;

    public ProjectBaseRulesEditorViewModel(
        ProjectBaseBinding binding,
        string currentDocumentPath,
        IFileNameParser parser,
        IFamilyManagerDialogService dialogService)
    {
        _parser = parser;
        _dialogService = dialogService;

        LoadBinding(binding);
        CurrentFilePath = currentDocumentPath;
        RefreshPreviewAndValidation();
    }

    private void LoadBinding(ProjectBaseBinding binding)
    {
        Blocks.Clear();
        foreach (var block in binding.Template?.Blocks ?? [])
        {
            var item = new FileNameBlockItem
            {
                Index = block.Index,
                Field = block.Field,
                ParseRule = block.ParseRule
            };
            item.PropertyChanged += OnBlockPropertyChanged;
            Blocks.Add(item);
        }

        FieldLibrary.Clear();
        foreach (var field in binding.FieldLibrary ?? [])
        {
            var item = FieldDefinitionItem.FromModel(field);
            item.PropertyChanged += OnFieldPropertyChanged;
            FieldLibrary.Add(item);
        }
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileNameBlockItem.Field) or nameof(FileNameBlockItem.ParseRule))
            RefreshPreviewAndValidation();
    }

    private void OnFieldPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshPreviewAndValidation();

    partial void OnCurrentFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentProjectName));
        RefreshPreviewAndValidation();
    }

    [RelayCommand]
    private void AddBlock()
    {
        var index = Blocks.Count;
        var item = new FileNameBlockItem
        {
            Index = index,
            Field = $"field{index}",
            ParseRule = ParseRule.DefaultDelimiter("-", index + 1)
        };
        item.PropertyChanged += OnBlockPropertyChanged;
        Blocks.Add(item);
        SelectedBlock = item;
        RefreshPreviewAndValidation();
    }

    [RelayCommand]
    private void RemoveBlock()
    {
        if (SelectedBlock is null) return;

        SelectedBlock.PropertyChanged -= OnBlockPropertyChanged;
        Blocks.Remove(SelectedBlock);
        SelectedBlock = null;
        SelectedBlockIndex = -1;
        RenumberBlocks();
        RefreshPreviewAndValidation();
    }

    [RelayCommand]
    private void MoveBlockUp()
    {
        if (SelectedBlockIndex <= 0) return;
        Blocks.Move(SelectedBlockIndex, SelectedBlockIndex - 1);
        RenumberBlocks();
        SelectedBlockIndex--;
        RefreshPreviewAndValidation();
    }

    [RelayCommand]
    private void MoveBlockDown()
    {
        if (SelectedBlockIndex < 0 || SelectedBlockIndex >= Blocks.Count - 1) return;
        Blocks.Move(SelectedBlockIndex, SelectedBlockIndex + 1);
        RenumberBlocks();
        SelectedBlockIndex++;
        RefreshPreviewAndValidation();
    }

    private void RenumberBlocks()
    {
        for (var i = 0; i < Blocks.Count; i++)
            Blocks[i].Index = i;
    }

    [RelayCommand]
    private void OpenParseRuleEditor()
    {
        if (SelectedBlock is null) return;

        var precedingRules = Blocks
            .OrderBy(b => b.Index)
            .TakeWhile(b => b.Index != SelectedBlock.Index)
            .Select(b => b.ParseRule)
            .ToList();

        try
        {
            var vm = new ParseRuleViewModel(SelectedBlock.ParseRule, CurrentFilePath, precedingRules);
            bool? dialogResult = null;
            Action<bool?>? closeHandler = result => dialogResult = result;
            vm.RequestClose += closeHandler;
            try
            {
                _dialogService.ShowParseRuleEditor(vm);
            }
            finally
            {
                vm.RequestClose -= closeHandler;
            }

            if (dialogResult != true) return;

            SelectedBlock.ParseRule = vm.BuildRule();
            SelectedBlock.RefreshParseRuleDisplay();
            RefreshPreviewAndValidation();
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope("ProjectBaseRulesEditor",
                ("Method", nameof(OpenParseRuleEditor)));
            SmartConLogger.Error($"OpenParseRuleEditor failed:\n{ex}");
            _dialogService.ShowError(
                LocalizationService.GetString("FM_Error_Title") ?? "Error",
                string.Format(LocalizationService.GetString("FM_PBase_ParseRuleError") ?? "Parse rule editor error:\n\n{0}",
                    $"{ex.Message}\n\n{ex.StackTrace}"));
        }
    }

    [RelayCommand]
    private void OpenFieldLibrary()
    {
        try
        {
            var oldFieldNames = FieldLibrary.Select(f => f.Name).ToList();
            var oldFieldNameSet = new HashSet<string>(oldFieldNames, StringComparer.OrdinalIgnoreCase);

            var vm = new FieldLibraryViewModel(_dialogService);
            foreach (var field in FieldLibrary)
                vm.Fields.Add(FieldDefinitionItem.FromModel(field.ToModel()));

            bool? dialogResult = null;
            Action<bool?>? closeHandler = result => dialogResult = result;
            vm.RequestClose += closeHandler;
            try
            {
                _dialogService.ShowFieldLibrary(vm);
            }
            finally
            {
                vm.RequestClose -= closeHandler;
            }

            if (dialogResult != true) return;

            var newFields = vm.Fields.ToList();
            var newFieldNames = newFields.Select(f => f.Name).ToList();
            var newFieldNameSet = new HashSet<string>(newFieldNames, StringComparer.OrdinalIgnoreCase);

            var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var minCount = Math.Min(oldFieldNames.Count, newFieldNames.Count);
            for (var i = 0; i < minCount; i++)
            {
                var oldName = oldFieldNames[i];
                var newName = newFieldNames[i];
                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Detect a genuine rename: the old name disappeared, the new name appeared,
                // and it occupies the same slot. This avoids false renames when a field is
                // deleted and a different one is inserted at the same index.
                if (!oldFieldNameSet.Contains(newName) && !newFieldNameSet.Contains(oldName))
                    renameMap[oldName] = newName;
            }

            while (FieldLibrary.Count > newFields.Count)
            {
                FieldLibrary[^1].PropertyChanged -= OnFieldPropertyChanged;
                FieldLibrary.RemoveAt(FieldLibrary.Count - 1);
            }

            for (var i = 0; i < FieldLibrary.Count; i++)
            {
                var source = newFields[i];
                var target = FieldLibrary[i];
                target.Name = source.Name;
                target.DisplayName = source.DisplayName;
                target.Description = source.Description;
                target.ValidationMode = source.ValidationMode;
                target.AllowedValues = source.AllowedValues;
                target.MinLength = source.MinLength;
                target.MaxLength = source.MaxLength;
            }

            for (var i = FieldLibrary.Count; i < newFields.Count; i++)
            {
                var field = newFields[i];
                field.PropertyChanged += OnFieldPropertyChanged;
                FieldLibrary.Add(field);
            }

            foreach (var block in Blocks)
            {
                if (string.IsNullOrEmpty(block.Field))
                    continue;

                if (renameMap.TryGetValue(block.Field, out var renamed))
                {
                    block.Field = renamed;
                    continue;
                }

                if (!FieldLibrary.Any(f => string.Equals(f.Name, block.Field, StringComparison.OrdinalIgnoreCase)))
                    block.Field = string.Empty;
            }

            RefreshPreviewAndValidation();
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope("ProjectBaseRulesEditor",
                ("Method", nameof(OpenFieldLibrary)));
            SmartConLogger.Error($"OpenFieldLibrary failed:\n{ex}");
            _dialogService.ShowError(
                LocalizationService.GetString("FM_Error_Title") ?? "Error",
                string.Format(LocalizationService.GetString("FM_PBase_FieldLibraryError") ?? "Field library error:\n\n{0}",
                    $"{ex.Message}\n\n{ex.StackTrace}"));
        }
    }

    private void RefreshPreviewAndValidation()
    {
        if (string.IsNullOrWhiteSpace(CurrentFilePath))
        {
            // #174: unsaved document — nothing to validate against. Reset
            // every block to the neutral "not evaluated" state instead of
            // leaving the default green ✓ next to an empty file name.
            ResetBlocksEvaluation();
            PreviewCurrent = string.Empty;
            PreviewParsed = string.Empty;
            HasValidationError = false;
            ValidationMessage = string.Empty;
            return;
        }

        PreviewCurrent = Path.GetFileName(CurrentFilePath);

        var template = BuildTemplate();
        if (template.Blocks.Count == 0)
        {
            ResetBlocksEvaluation();
            PreviewParsed = string.Empty;
            HasValidationError = false;
            ValidationMessage = string.Empty;
            return;
        }

        var parsed = _parser.ParseBlocks(CurrentFilePath, template);
        foreach (var block in Blocks)
        {
            block.CurrentFieldValue = string.IsNullOrEmpty(block.Field)
                ? string.Empty
                : parsed.TryGetValue(block.Field, out var value) ? value : string.Empty;
        }

        var validation = _parser.ValidateDetailed(CurrentFilePath, template, FieldLibrary.Select(f => f.ToModel()).ToList());
        foreach (var bv in validation.Blocks)
        {
            var blockItem = Blocks.FirstOrDefault(b => b.Index == bv.Index);
            if (blockItem is null) continue;
            blockItem.IsValid = bv.IsValid;
            blockItem.ValidationError = bv.Error;
        }

        HasValidationError = !validation.IsValid;
        ValidationMessage = validation.IsValid
            ? string.Format(LocalizationService.GetString("FM_PBase_ValidationOk") ?? "Matched: {0}",
                string.Join(", ", Blocks.Select(b => $"{b.Field}={b.CurrentFieldValue}")))
            : validation.Summary;

        PreviewParsed = string.Join(template.Blocks.Count > 1 ? ", " : "",
            Blocks.Select(b => $"{b.Field}={b.CurrentFieldValue}"));
    }

    private void ResetBlocksEvaluation()
    {
        foreach (var block in Blocks)
        {
            block.CurrentFieldValue = string.Empty;
            block.IsValid = null;
            block.ValidationError = null;
        }
    }

    [RelayCommand]
    private void Ok()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    public ProjectBaseBinding? BuildBinding()
    {
        if (Blocks.Count == 0) return null;
        var template = BuildTemplate();
        var fields = FieldLibrary.Select(f => f.ToModel()).ToList();
        return new ProjectBaseBinding(template, fields);
    }

    private FileNameTemplate BuildTemplate()
    {
        return new FileNameTemplate
        {
            Blocks = Blocks.Select(b => new FileBlockDefinition
            {
                Index = b.Index,
                Field = b.Field ?? string.Empty,
                ParseRule = b.ParseRule
            }).ToList()
        };
    }
}
