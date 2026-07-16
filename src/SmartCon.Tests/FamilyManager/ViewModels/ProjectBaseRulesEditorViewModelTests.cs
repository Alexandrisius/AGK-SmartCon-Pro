using Moq;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels.ProjectBase;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class ProjectBaseRulesEditorViewModelTests
{
    private static ProjectBaseRulesEditorViewModel CreateVm(
        ProjectBaseBinding? binding = null,
        IFileNameParser? parser = null,
        IFamilyManagerDialogService? dialogService = null,
        string currentDocumentPath = "")
    {
        var parserMock = new Mock<IFileNameParser>();
        parserMock.Setup(p => p.ParseBlocks(It.IsAny<string>(), It.IsAny<FileNameTemplate>())).Returns(new Dictionary<string, string>());
        parserMock.Setup(p => p.ValidateDetailed(It.IsAny<string>(), It.IsAny<FileNameTemplate>(), It.IsAny<List<FieldDefinition>>()))
            .Returns(new ValidationResult(true, string.Empty, []));

        return new ProjectBaseRulesEditorViewModel(
            binding ?? ProjectBaseBinding.Empty,
            currentDocumentPath,
            parser ?? parserMock.Object,
            dialogService ?? new Mock<IFamilyManagerDialogService>().Object);
    }

    [Fact]
    public void BuildBinding_EmptyBlocks_ReturnsNull()
    {
        var vm = CreateVm();

        var result = vm.BuildBinding();

        Assert.Null(result);
    }

    [Fact]
    public void BuildBinding_WithBlocks_ReturnsTemplateAndFields()
    {
        var binding = new ProjectBaseBinding(
            new FileNameTemplate
            {
                Blocks =
                [
                    new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }
                ]
            },
            [new FieldDefinition { Name = "project", DisplayName = "Project", ValidationMode = ValidationMode.None }]);

        var vm = CreateVm(binding);

        var result = vm.BuildBinding();

        Assert.NotNull(result);
        Assert.Single(result.Template.Blocks);
        Assert.Equal("project", result.Template.Blocks[0].Field);
        Assert.Single(result.FieldLibrary);
        Assert.Equal("project", result.FieldLibrary[0].Name);
    }

    [Fact]
    public void AddBlock_AppendsBlockAndSelectsIt()
    {
        var vm = CreateVm();

        vm.AddBlockCommand.Execute(null);

        Assert.Single(vm.Blocks);
        Assert.Equal(vm.Blocks[0], vm.SelectedBlock);
        Assert.Equal(0, vm.SelectedBlock!.Index);
    }

    [Fact]
    public void RemoveBlock_RemovesSelectedBlock()
    {
        var vm = CreateVm();
        vm.AddBlockCommand.Execute(null);
        vm.AddBlockCommand.Execute(null);

        vm.RemoveBlockCommand.Execute(null);

        Assert.Single(vm.Blocks);
    }

    [Fact]
    public void MoveBlockUp_ReordersBlocks()
    {
        var vm = CreateVm();
        vm.AddBlockCommand.Execute(null);
        vm.AddBlockCommand.Execute(null);
        vm.SelectedBlockIndex = 1;

        vm.MoveBlockUpCommand.Execute(null);

        Assert.Equal(0, vm.SelectedBlockIndex);
        Assert.Equal(0, vm.Blocks[0].Index);
        Assert.Equal(1, vm.Blocks[1].Index);
    }

    [Fact]
    public void MoveBlockDown_ReordersBlocks()
    {
        var vm = CreateVm();
        vm.AddBlockCommand.Execute(null);
        vm.AddBlockCommand.Execute(null);
        vm.SelectedBlockIndex = 0;

        vm.MoveBlockDownCommand.Execute(null);

        Assert.Equal(1, vm.SelectedBlockIndex);
        Assert.Equal(0, vm.Blocks[0].Index);
        Assert.Equal(1, vm.Blocks[1].Index);
    }

    [Fact]
    public void RenumberBlocks_UpdatesIndexesAfterMove()
    {
        var vm = CreateVm();
        vm.AddBlockCommand.Execute(null);
        vm.AddBlockCommand.Execute(null);
        vm.AddBlockCommand.Execute(null);
        vm.SelectedBlockIndex = 2;

        vm.MoveBlockUpCommand.Execute(null);
        vm.MoveBlockUpCommand.Execute(null);

        Assert.Equal(0, vm.Blocks[0].Index);
        Assert.Equal(1, vm.Blocks[1].Index);
        Assert.Equal(2, vm.Blocks[2].Index);
    }

    [Fact]
    public void OkCommand_InvokesRequestCloseWithTrue()
    {
        var vm = CreateVm();
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.OkCommand.Execute(null);

        Assert.True(result);
    }

    [Fact]
    public void CancelCommand_InvokesRequestCloseWithFalse()
    {
        var vm = CreateVm();
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.CancelCommand.Execute(null);

        Assert.False(result);
    }

    [Fact]
    public void OpenFieldLibrary_RenamesField_UpdatesBlockReferences()
    {
        var binding = new ProjectBaseBinding(
            new FileNameTemplate
            {
                Blocks =
                [
                    new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }
                ]
            },
            [new FieldDefinition { Name = "project", DisplayName = "Project", ValidationMode = ValidationMode.None }]);

        var dialogService = new Mock<IFamilyManagerDialogService>();
        dialogService
            .Setup(d => d.ShowFieldLibrary(It.IsAny<object>()))
            .Returns((object vm) =>
            {
                if (vm is FieldLibraryViewModel fieldLibraryVm)
                {
                    fieldLibraryVm.Fields[0].Name = "renamed_project";
                    fieldLibraryVm.OkCommand.Execute(null);
                }

                return true;
            });

        var vm = CreateVm(binding, dialogService: dialogService.Object);

        vm.OpenFieldLibraryCommand.Execute(null);

        Assert.Equal("renamed_project", vm.FieldLibrary[0].Name);
        Assert.Equal("renamed_project", vm.Blocks[0].Field);
    }

    [Fact]
    public void OpenFieldLibrary_DeletesField_ClearsBlockField()
    {
        var binding = new ProjectBaseBinding(
            new FileNameTemplate
            {
                Blocks =
                [
                    new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }
                ]
            },
            [new FieldDefinition { Name = "project", DisplayName = "Project", ValidationMode = ValidationMode.None }]);

        var dialogService = new Mock<IFamilyManagerDialogService>();
        dialogService
            .Setup(d => d.ShowFieldLibrary(It.IsAny<object>()))
            .Returns((object vm) =>
            {
                if (vm is FieldLibraryViewModel fieldLibraryVm)
                {
                    fieldLibraryVm.Fields.RemoveAt(0);
                    fieldLibraryVm.OkCommand.Execute(null);
                }

                return true;
            });

        var vm = CreateVm(binding, dialogService: dialogService.Object);

        vm.OpenFieldLibraryCommand.Execute(null);

        Assert.Empty(vm.FieldLibrary);
        Assert.Equal(string.Empty, vm.Blocks[0].Field);
    }
}

[Collection("Localization")]
public sealed class FileNameBlockItemTests : IDisposable
{
    private readonly Language _originalLanguage;

    public FileNameBlockItemTests()
    {
        _originalLanguage = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.EN);
    }

    public void Dispose() => LocalizationService.SetLanguage(_originalLanguage);

    [Fact]
    public void ParseRuleDisplay_DelimiterSingleSegment()
    {
        var item = new FileNameBlockItem
        {
            ParseRule = new ParseRule
            {
                Mode = ParseMode.DelimiterSegment,
                Delimiter = "-",
                SegmentIndex = 2,
                SegmentCount = 1
            }
        };

        Assert.Contains("#2", item.ParseRuleDisplay);
    }

    [Fact]
    public void ParseRuleDisplay_DelimiterMultiSegment()
    {
        var item = new FileNameBlockItem
        {
            ParseRule = new ParseRule
            {
                Mode = ParseMode.DelimiterSegment,
                Delimiter = "-",
                SegmentIndex = 1,
                SegmentCount = 2
            }
        };

        Assert.Contains("#1-2", item.ParseRuleDisplay);
    }

    [Fact]
    public void ParseRuleDisplay_FixedWidth()
    {
        var item = new FileNameBlockItem
        {
            ParseRule = new ParseRule
            {
                Mode = ParseMode.FixedWidth,
                CharOffset = 0,
                CharCount = 3
            }
        };

        Assert.Contains("3", item.ParseRuleDisplay);
    }

    [Fact]
    public void ParseRuleDisplay_Remainder()
    {
        var item = new FileNameBlockItem
        {
            ParseRule = new ParseRule { Mode = ParseMode.Remainder }
        };

        Assert.Contains("Remainder", item.ParseRuleDisplay);
    }
}

[Collection("Localization")]
public sealed class FieldDefinitionItemTests : IDisposable
{
    private readonly Language _originalLanguage;

    public FieldDefinitionItemTests()
    {
        _originalLanguage = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.EN);
    }

    public void Dispose() => LocalizationService.SetLanguage(_originalLanguage);

    [Fact]
    public void ToModel_RoundTrip_PreservesValues()
    {
        var item = new FieldDefinitionItem
        {
            Name = "project",
            DisplayName = "Project",
            Description = "Project code",
            ValidationMode = ValidationMode.AllowedValues,
            AllowedValues = ["A", "B"],
            MinLength = 1,
            MaxLength = 10
        };

        var model = item.ToModel();
        var restored = FieldDefinitionItem.FromModel(model);

        Assert.Equal("project", restored.Name);
        Assert.Equal("Project", restored.DisplayName);
        Assert.Equal("Project code", restored.Description);
        Assert.Equal(ValidationMode.AllowedValues, restored.ValidationMode);
        Assert.Equal(2, restored.AllowedValues.Count);
        Assert.Equal(1, restored.MinLength);
        Assert.Equal(10, restored.MaxLength);
    }

    [Fact]
    public void ValidationModeDisplay_AllowedValues()
    {
        var item = new FieldDefinitionItem { ValidationMode = ValidationMode.AllowedValues };

        Assert.Equal("List", item.ValidationModeDisplay);
    }
}

public sealed class FamilyManagerParseRuleViewModelTests
{
    private static ParseRuleViewModel CreateVm(
        ParseRule? initialRule = null,
        string previewFileName = "PRJ-S0-DEV.rvt",
        List<ParseRule>? precedingRules = null)
    {
        return new ParseRuleViewModel(
            initialRule ?? ParseRule.DefaultDelimiter(),
            previewFileName,
            precedingRules ?? []);
    }

    [Fact]
    public void BuildRule_DelimiterSegment_CreatesCorrectRule()
    {
        var vm = CreateVm();
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "_";
        vm.SegmentIndex = 3;

        var rule = vm.BuildRule();

        Assert.Equal(ParseMode.DelimiterSegment, rule.Mode);
        Assert.Equal("_", rule.Delimiter);
        Assert.Equal(3, rule.SegmentIndex);
    }

    [Fact]
    public void Preview_DelimiterSegment_ExtractsCorrectValue()
    {
        var vm = CreateVm(previewFileName: "PRJ-S0-DEV.rvt");
        vm.Mode = ParseMode.DelimiterSegment;
        vm.Delimiter = "-";
        vm.SegmentIndex = 2;

        Assert.Equal("S0", vm.PreviewValue);
    }

    [Fact]
    public void OkCommand_InvokesRequestCloseWithTrue()
    {
        var vm = CreateVm();
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.OkCommand.Execute(null);

        Assert.True(result);
    }
}

public sealed class FieldLibraryViewModelTests
{
    private static FieldLibraryViewModel CreateVm(IFamilyManagerDialogService? dialogService = null) =>
        new(dialogService ?? new Mock<IFamilyManagerDialogService>().Object);

    [Fact]
    public void AddField_AppendsFieldAndSelectsIt()
    {
        var vm = CreateVm();

        vm.AddFieldCommand.Execute(null);

        Assert.Single(vm.Fields);
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void RemoveField_RemovesSelectedField()
    {
        var vm = CreateVm();
        vm.AddFieldCommand.Execute(null);
        vm.AddFieldCommand.Execute(null);
        vm.SelectedIndex = 1;

        vm.RemoveFieldCommand.Execute(null);

        Assert.Single(vm.Fields);
    }

    [Fact]
    public void DuplicateField_CreatesCopyNextToOriginal()
    {
        var vm = CreateVm();
        vm.AddFieldCommand.Execute(null);
        vm.Fields[0].Name = "project";
        vm.SelectedIndex = 0;

        vm.DuplicateFieldCommand.Execute(null);

        Assert.Equal(2, vm.Fields.Count);
        Assert.Equal("project_copy", vm.Fields[1].Name);
        Assert.Equal(1, vm.SelectedIndex);
    }

    [Fact]
    public void OkCommand_InvokesRequestCloseWithTrue()
    {
        var vm = CreateVm();
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.OkCommand.Execute(null);

        Assert.True(result);
    }
}

public sealed class AllowedValuesViewModelTests
{
    [Fact]
    public void ApplyTo_UpdatesAllowedValuesAndValidationModeAndTrimsValues()
    {
        var field = new FieldDefinitionItem { Name = "project", ValidationMode = ValidationMode.None };
        var vm = new AllowedValuesViewModel(field);

        vm.ValidationMode = ValidationMode.AllowedValues;
        vm.AddValueCommand.Execute(null);
        vm.Values[0].Value = "  ABC  ";

        vm.ApplyTo(field);

        Assert.Equal(ValidationMode.AllowedValues, field.ValidationMode);
        Assert.Contains("ABC", field.AllowedValues);
    }

    [Fact]
    public void Constructor_LoadsValuesFromFieldItem()
    {
        var field = new FieldDefinitionItem
        {
            Name = "status",
            ValidationMode = ValidationMode.Contains,
            AllowedValues = ["AR", "ME"]
        };

        var vm = new AllowedValuesViewModel(field);

        Assert.Equal(2, vm.Values.Count);
        Assert.Equal("AR", vm.Values[0].Value);
        Assert.Equal("ME", vm.Values[1].Value);
        Assert.Equal(ValidationMode.Contains, vm.ValidationMode);
    }

    [Fact]
    public void AddValueCommand_AddsEditableItemAndSetsFocusItem()
    {
        var field = new FieldDefinitionItem { Name = "project", ValidationMode = ValidationMode.None };
        var vm = new AllowedValuesViewModel(field);

        vm.AddValueCommand.Execute(null);

        Assert.Single(vm.Values);
        Assert.NotNull(vm.SelectedValue);
        Assert.Equal(vm.Values[0], vm.SelectedValue);
        Assert.Equal(vm.Values[0], vm.FocusItem);
    }

    [Fact]
    public void RemoveValueCommand_RemovesSelectedValue()
    {
        var field = new FieldDefinitionItem
        {
            Name = "status",
            ValidationMode = ValidationMode.AllowedValues,
            AllowedValues = ["S0", "S1"]
        };
        var vm = new AllowedValuesViewModel(field);

        vm.SelectedValue = vm.Values[1];
        vm.RemoveValueCommand.Execute(null);

        Assert.Single(vm.Values);
        Assert.Equal("S0", vm.Values[0].Value);
    }

    [Fact]
    public void ShowValuesList_TrueForContains()
    {
        var field = new FieldDefinitionItem { Name = "status", ValidationMode = ValidationMode.Contains };
        var vm = new AllowedValuesViewModel(field);

        Assert.True(vm.ShowValuesList);
        Assert.False(vm.ShowLengthFields);
    }

    [Fact]
    public void MoveUpCommand_MovesSelectedValueUp()
    {
        var field = new FieldDefinitionItem
        {
            Name = "status",
            ValidationMode = ValidationMode.AllowedValues,
            AllowedValues = ["A", "B", "C"]
        };
        var vm = new AllowedValuesViewModel(field);

        vm.SelectedValue = vm.Values[1];
        vm.MoveUpCommand.Execute(null);

        Assert.Equal("B", vm.Values[0].Value);
        Assert.Equal("A", vm.Values[1].Value);
    }

    [Fact]
    public void OkCommand_InvokesRequestCloseWithTrue()
    {
        var field = new FieldDefinitionItem { Name = "project" };
        var vm = new AllowedValuesViewModel(field);
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.OkCommand.Execute(null);

        Assert.True(result);
    }
}
