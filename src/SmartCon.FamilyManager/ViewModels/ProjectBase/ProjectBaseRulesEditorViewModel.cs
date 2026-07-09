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

    partial void OnCurrentFilePathChanged(string value) => RefreshPreviewAndValidation();

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
            vm.RequestClose += result => dialogResult = result;

            _dialogService.ShowParseRuleEditor(vm);

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
            var vm = new FieldLibraryViewModel(_dialogService);
            foreach (var field in FieldLibrary)
                vm.Fields.Add(field);

            bool? dialogResult = null;
            vm.RequestClose += result => dialogResult = result;

            _dialogService.ShowFieldLibrary(vm);

            if (dialogResult != true) return;

            foreach (var field in FieldLibrary)
                field.PropertyChanged -= OnFieldPropertyChanged;
            FieldLibrary.Clear();

            foreach (var field in vm.Fields)
            {
                field.PropertyChanged += OnFieldPropertyChanged;
                FieldLibrary.Add(field);
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
        if (string.IsNullOrEmpty(CurrentFilePath))
        {
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
            PreviewParsed = string.Empty;
            HasValidationError = false;
            ValidationMessage = string.Empty;
            return;
        }

        var parsed = _parser.ParseBlocks(CurrentFilePath, template);
        foreach (var block in Blocks)
        {
            block.CurrentFieldValue = parsed.TryGetValue(block.Field, out var value) ? value : string.Empty;
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
                Field = b.Field,
                ParseRule = b.ParseRule
            }).ToList()
        };
    }
}
