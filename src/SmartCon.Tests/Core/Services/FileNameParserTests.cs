using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class FileNameParserTests
{
    private readonly IFileNameParser _parser = new FileNameParser();

    private static FileNameTemplate CreateTemplate(string delimiter = "-")
    {
        return new FileNameTemplate
        {
            Delimiter = delimiter,
            Blocks =
            [
                new FileBlockDefinition { Index = 0, Role = "project" },
                new FileBlockDefinition { Index = 1, Role = "originator" },
                new FileBlockDefinition { Index = 2, Role = "volume" },
                new FileBlockDefinition { Index = 3, Role = "level" },
                new FileBlockDefinition { Index = 4, Role = "type" },
                new FileBlockDefinition { Index = 5, Role = "discipline" },
                new FileBlockDefinition { Index = 6, Role = "number" },
                new FileBlockDefinition { Index = 7, Role = "status" }
            ],
            StatusMappings =
            [
                new StatusMapping { WipValue = "S0", SharedValue = "S1" }
            ]
        };
    }

    [Fact]
    public void ParseBlocks_CorrectName_ReturnsAllBlocks()
    {
        var template = CreateTemplate();
        var result = _parser.ParseBlocks("0001-PPR-001-00001-01-AR-M3-S0.rvt", template);
        Assert.Equal(8, result.Count);
        Assert.Equal("0001", result["project"]);
        Assert.Equal("PPR", result["originator"]);
        Assert.Equal("S0", result["status"]);
    }

    [Fact]
    public void ParseBlocks_NameWithExtension_ExtensionRemoved()
    {
        var template = CreateTemplate();
        var result = _parser.ParseBlocks("0001-PPR-001-00001-01-AR-M3-S0.rvt", template);
        Assert.DoesNotContain(".rvt", result.Values);
    }

    [Fact]
    public void ParseBlocks_MorePartsThanBlocks_ExtraIgnored()
    {
        var template = CreateTemplate();
        var result = _parser.ParseBlocks("0001-PPR-001-00001-01-AR-M3-S0-EXTRA.rvt", template);
        Assert.Equal(8, result.Count);
    }

    [Fact]
    public void ParseBlocks_FewerPartsThanBlocks_OnlyAvailableReturned()
    {
        var template = CreateTemplate();
        var result = _parser.ParseBlocks("0001-PPR-001.rvt", template);
        Assert.Equal(3, result.Count);
        Assert.Equal("0001", result["project"]);
    }

    [Fact]
    public void TransformStatus_S0ToS1_ReturnsTransformed()
    {
        var template = CreateTemplate();
        var result = _parser.TransformStatus("0001-PPR-001-00001-01-AR-M3-S0.rvt", template);
        Assert.Equal("0001-PPR-001-00001-01-AR-M3-S1.rvt", result);
    }

    [Fact]
    public void TransformStatus_MultipleMappings_CurrentWip_ReturnsShared()
    {
        var template = CreateTemplate();
        template = template with
        {
            StatusMappings =
            [
                new StatusMapping { WipValue = "S0", SharedValue = "S1" },
                new StatusMapping { WipValue = "S1", SharedValue = "S2" }
            ]
        };
        var result = _parser.TransformStatus("0001-PPR-001-00001-01-AR-M3-S1.rvt", template);
        Assert.Equal("0001-PPR-001-00001-01-AR-M3-S2.rvt", result);
    }

    [Fact]
    public void TransformStatus_StatusNotInMapping_ReturnsNull()
    {
        var template = CreateTemplate();
        var result = _parser.TransformStatus("0001-PPR-001-00001-01-AR-M3-S9.rvt", template);
        Assert.Null(result);
    }

    [Fact]
    public void TransformStatus_NoStatusBlock_ReturnsNull()
    {
        var template = CreateTemplate();
        template = template with
        {
            Blocks = [new FileBlockDefinition { Index = 0, Role = "project" }]
        };
        var result = _parser.TransformStatus("0001.rvt", template);
        Assert.Null(result);
    }

    [Fact]
    public void Validate_ValidConfig_ReturnsTrue()
    {
        var template = CreateTemplate();
        var (isValid, error) = _parser.Validate("0001-PPR-001-00001-01-AR-M3-S0.rvt", template);
        Assert.True(isValid);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Validate_EmptyDelimiter_ReturnsFalse()
    {
        var template = CreateTemplate() with { Delimiter = "" };
        var (isValid, _) = _parser.Validate("file.rvt", template);
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_NoBlocks_ReturnsFalse()
    {
        var template = new FileNameTemplate { Delimiter = "-", StatusMappings = [new StatusMapping { WipValue = "S0", SharedValue = "S1" }] };
        var (isValid, _) = _parser.Validate("file.rvt", template);
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_NoStatusBlock_ReturnsFalse()
    {
        var template = new FileNameTemplate
        {
            Delimiter = "-",
            Blocks = [new FileBlockDefinition { Index = 0, Role = "project" }],
            StatusMappings = [new StatusMapping { WipValue = "S0", SharedValue = "S1" }]
        };
        var (isValid, _) = _parser.Validate("file.rvt", template);
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_EmptyStatusMappings_ReturnsFalse()
    {
        var template = CreateTemplate() with { StatusMappings = [] };
        var (isValid, _) = _parser.Validate("0001-PPR-001-00001-01-AR-M3-S0.rvt", template);
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_DuplicateWipValues_ReturnsFalse()
    {
        var template = CreateTemplate() with
        {
            StatusMappings =
            [
                new StatusMapping { WipValue = "S0", SharedValue = "S1" },
                new StatusMapping { WipValue = "S0", SharedValue = "S2" }
            ]
        };
        var (isValid, _) = _parser.Validate("0001-PPR-001-00001-01-AR-M3-S0.rvt", template);
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_FileNameShorterThanBlocks_ReturnsFalse()
    {
        var template = CreateTemplate();
        var (isValid, _) = _parser.Validate("0001-PPR.rvt", template);
        Assert.False(isValid);
    }

    [Fact]
    public void Validate_StatusValueNotInWipValues_ReturnsFalse()
    {
        var template = CreateTemplate();
        var (isValid, error) = _parser.Validate("0001-PPR-001-00001-01-AR-M3-S9.rvt", template);
        Assert.False(isValid);
        Assert.Contains("S9", error);
    }

    [Fact]
    public void TransformStatus_UnderscoreDelimiter_Works()
    {
        var template = new FileNameTemplate
        {
            Delimiter = "_",
            Blocks =
            [
                new FileBlockDefinition { Index = 0, Role = "project" },
                new FileBlockDefinition { Index = 1, Role = "discipline" },
                new FileBlockDefinition { Index = 2, Role = "type" },
                new FileBlockDefinition { Index = 3, Role = "number" },
                new FileBlockDefinition { Index = 4, Role = "status" }
            ],
            StatusMappings = [new StatusMapping { WipValue = "WIP", SharedValue = "SHARED" }]
        };
        var result = _parser.TransformStatus("PROJ001_AR_PLAN_01_WIP.rvt", template);
        Assert.Equal("PROJ001_AR_PLAN_01_SHARED.rvt", result);
    }
}
