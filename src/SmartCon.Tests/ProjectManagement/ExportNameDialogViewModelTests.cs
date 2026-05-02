using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Implementation;
using SmartCon.ProjectManagement.ViewModels;
using Xunit;

namespace SmartCon.Tests.ProjectManagement;

[Collection("ServiceHost")]
public sealed class ExportNameDialogViewModelTests
{
    private static ExportNameDialogViewModel CreateVm(
        string fileName, string validationErrors, List<FileBlockDefinition> blocks,
        List<FieldDefinition> fieldLibrary, List<ExportMapping> exportMappings)
    {
        ServiceHost.Reset();
        ServiceHost.Initialize(t =>
        {
            if (t == typeof(SmartCon.Core.Services.Interfaces.IFileNameParser))
                return new FileNameParser();
            throw new InvalidOperationException($"Unknown service: {t}");
        });

        return new ExportNameDialogViewModel(fileName, validationErrors, blocks, fieldLibrary, exportMappings);
    }

    private static List<FileBlockDefinition> CreateBlocks(params (int Index, string Field)[] defs)
    {
        return defs.Select(d => new FileBlockDefinition
        {
            Index = d.Index,
            Field = d.Field,
            ParseRule = ParseRule.DefaultDelimiter("-", 0)
        }).ToList();
    }

    [Fact]
    public void Constructor_InitializesFieldsFromBlocks()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "status", AllowedValues = ["S0", "S1"], ValidationMode = ValidationMode.AllowedValues }
        };

        var vm = CreateVm("PRJ-S0.rvt", "", blocks, fieldLibrary, []);

        Assert.Equal(2, vm.Fields.Count);
        Assert.Equal("project", vm.Fields[0].Field);
        Assert.Equal("status", vm.Fields[1].Field);
    }

    [Fact]
    public void Constructor_AppliesExportMappings()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "status", AllowedValues = ["S0", "S1"], ValidationMode = ValidationMode.AllowedValues }
        };
        var mappings = new List<ExportMapping>
        {
            new() { Field = "status", SourceValue = "S0", TargetValue = "S1" }
        };

        var vm = CreateVm("PRJ-S0.rvt", "", blocks, fieldLibrary, mappings);

        Assert.Equal("S1", vm.Fields[1].Value);
    }

    [Fact]
    public void PreviewFileName_JoinsFieldValuesWithDash()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var vm = CreateVm("PRJ-S0.rvt", "", blocks, [], []);

        Assert.Equal("PRJ-S0", vm.PreviewFileName);
    }

    [Fact]
    public void PreviewFileName_UpdatesWhenFieldValueChanges()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var vm = CreateVm("PRJ-S0.rvt", "", blocks, [], []);

        vm.Fields[1].Value = "S1";

        Assert.Equal("PRJ-S1", vm.PreviewFileName);
    }

    [Fact]
    public void Validation_BlocksEmptyField()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var vm = CreateVm("PRJ-S0.rvt", "", blocks, [], []);

        vm.Fields[1].Value = "";

        Assert.False(vm.IsValid);
        Assert.Contains("status", vm.FieldErrors);
    }

    [Fact]
    public void Validation_BlocksDisallowedValue()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "status", AllowedValues = ["S0", "S1"], ValidationMode = ValidationMode.AllowedValues }
        };

        var vm = CreateVm("PRJ-S0.rvt", "", blocks, fieldLibrary, []);

        vm.Fields[1].Value = "XX";

        Assert.False(vm.IsValid);
        Assert.Contains("status", vm.FieldErrors);
    }

    [Fact]
    public void Validation_AllowsValidValue()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "status", AllowedValues = ["S0", "S1"], ValidationMode = ValidationMode.AllowedValues }
        };

        var vm = CreateVm("PRJ-S0.rvt", "", blocks, fieldLibrary, []);

        vm.Fields[0].Value = "PRJ";
        vm.Fields[1].Value = "S1";

        Assert.True(vm.IsValid);
        Assert.Empty(vm.FieldErrors);
    }

    [Fact]
    public void Validation_CharCount_TooShort()
    {
        var blocks = CreateBlocks((0, "code"));
        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "code", ValidationMode = ValidationMode.CharCount, MinLength = 3, MaxLength = 5 }
        };

        var vm = CreateVm("AB.rvt", "", blocks, fieldLibrary, []);

        vm.Fields[0].Value = "AB";

        Assert.False(vm.IsValid);
        Assert.Contains("code", vm.FieldErrors);
    }

    [Fact]
    public void Validation_CharCount_TooLong()
    {
        var blocks = CreateBlocks((0, "code"));
        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "code", ValidationMode = ValidationMode.CharCount, MinLength = 2, MaxLength = 3 }
        };

        var vm = CreateVm("ABCDE.rvt", "", blocks, fieldLibrary, []);

        vm.Fields[0].Value = "ABCDE";

        Assert.False(vm.IsValid);
        Assert.Contains("code", vm.FieldErrors);
    }

    [Fact]
    public void GetFieldValues_ReturnsDictionary()
    {
        var blocks = CreateBlocks((0, "project"), (1, "status"));
        var vm = CreateVm("PRJ-S0.rvt", "", blocks, [], []);

        var values = vm.GetFieldValues();

        Assert.Equal("PRJ", values["project"]);
        Assert.Equal("S0", values["status"]);
    }

    [Fact]
    public void ExportCommand_WhenValid_InvokesRequestCloseWithTrue()
    {
        var blocks = CreateBlocks((0, "project"));
        var vm = CreateVm("PRJ.rvt", "", blocks, [], []);
        vm.Fields[0].Value = "PRJ";

        bool? result = null;
        vm.RequestClose += r => result = r;
        vm.ExportCommand.Execute(null);

        Assert.True(result);
    }

    [Fact]
    public void ExportCommand_WhenInvalid_DoesNotInvokeRequestClose()
    {
        var blocks = CreateBlocks((0, "project"));
        var vm = CreateVm("PRJ.rvt", "", blocks, [], []);
        vm.Fields[0].Value = "";

        bool? result = null;
        vm.RequestClose += r => result = r;
        vm.ExportCommand.Execute(null);

        Assert.Null(result);
    }

    [Fact]
    public void CancelCommand_InvokesRequestCloseWithFalse()
    {
        var blocks = CreateBlocks((0, "project"));
        var vm = CreateVm("PRJ.rvt", "", blocks, [], []);

        bool? result = null;
        vm.RequestClose += r => result = r;
        vm.CancelCommand.Execute(null);

        Assert.False(result);
    }
}

[CollectionDefinition("ServiceHost", DisableParallelization = true)]
public sealed class ServiceHostCollection;
