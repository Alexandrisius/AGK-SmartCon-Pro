using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class FileNameParserTests
{
    private readonly FileNameParser _parser = new();

    private static FileNameTemplate CreateTemplate(params (int index, string field, ParseRule rule)[] blocks)
    {
        return new FileNameTemplate
        {
            Blocks = blocks.Select(b => new FileBlockDefinition
            {
                Index = b.index,
                Field = b.field,
                ParseRule = b.rule
            }).ToList()
        };
    }

    private static ParseRule DelimSeg(string delimiter, int index)
        => new() { Mode = ParseMode.DelimiterSegment, Delimiter = delimiter, SegmentIndex = index };

    private static ParseRule FixedW(int count)
        => new() { Mode = ParseMode.FixedWidth, CharCount = count };

    private static ParseRule Between(string open, string close)
        => new() { Mode = ParseMode.BetweenMarkers, OpenMarker = open, CloseMarker = close };

    private static ParseRule AfterM(string marker)
        => new() { Mode = ParseMode.AfterMarker, Marker = marker };

    private static ParseRule RemainderRule()
        => new() { Mode = ParseMode.Remainder };

    #region ApplyParseRule — unit tests

    [Fact]
    public void ApplyParseRule_DelimiterSegment_TakesCorrectSegment()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", DelimSeg("-", 1));
        Assert.Equal("B", value);
        Assert.Equal("A-C", remaining);
    }

    [Fact]
    public void ApplyParseRule_DelimiterSegment_FirstSegment()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", DelimSeg("-", 0));
        Assert.Equal("A", value);
        Assert.Equal("B-C", remaining);
    }

    [Fact]
    public void ApplyParseRule_DelimiterSegment_IndexOutOfRange_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B", DelimSeg("-", 5));
        Assert.Equal(string.Empty, value);
        Assert.Equal("A-B", remaining);
    }

    [Fact]
    public void ApplyParseRule_FixedWidth_TakesCorrectChars()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ-S1", FixedW(3));
        Assert.Equal("PRJ", value);
        Assert.Equal("-S1", remaining);
    }

    [Fact]
    public void ApplyParseRule_FixedWidth_ExceedsLength_TakesAll()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("AB", FixedW(5));
        Assert.Equal("AB", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_ExtractsValue()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ(S1)-001", Between("(", ")"));
        Assert.Equal("S1", value);
        Assert.Equal("PRJ-001", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_NoOpenMarker_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ-S1", Between("(", ")"));
        Assert.Equal(string.Empty, value);
        Assert.Equal("PRJ-S1", remaining);
    }

    [Fact]
    public void ApplyParseRule_AfterMarker_TakesAfterMarker()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ-S1-DWG", AfterM("-S1-"));
        Assert.Equal("DWG", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_Remainder_TakesAll()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("rest-text", RemainderRule());
        Assert.Equal("rest-text", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_EmptyInput_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("", DelimSeg("-", 0));
        Assert.Equal(string.Empty, value);
        Assert.Equal(string.Empty, remaining);
    }

    #endregion

    #region ParseBlocks — sequential consumptive

    [Fact]
    public void ParseBlocks_DelimiterSegments_ParsesAll()
    {
        var template = CreateTemplate(
            (0, "project", DelimSeg("-", 0)),
            (1, "status", DelimSeg("-", 0)),
            (2, "type", DelimSeg("-", 0)));

        var result = _parser.ParseBlocks("PRJ-S1-DWG", template);

        Assert.Equal("PRJ", result["project"]);
        Assert.Equal("S1", result["status"]);
        Assert.Equal("DWG", result["type"]);
    }

    [Fact]
    public void ParseBlocks_MixedModes_FixedWidthThenDelimiter()
    {
        var template = CreateTemplate(
            (0, "code", FixedW(3)),
            (1, "status", DelimSeg("-", 1)));

        var result = _parser.ParseBlocks("PRJ-S1-DWG", template);

        Assert.Equal("PRJ", result["code"]);
        Assert.Equal("S1", result["status"]);
    }

    [Fact]
    public void ParseBlocks_BetweenMarkers_Extracts()
    {
        var template = CreateTemplate(
            (0, "code", FixedW(3)),
            (1, "status", Between("(", ")")),
            (2, "rest", RemainderRule()));

        var result = _parser.ParseBlocks("PRJ(S1)-001", template);

        Assert.Equal("PRJ", result["code"]);
        Assert.Equal("S1", result["status"]);
        Assert.Equal("-001", result["rest"]);
    }

    [Fact]
    public void ParseBlocks_EmptyFileName_ReturnsEmptyDict()
    {
        var template = CreateTemplate((0, "f", DelimSeg("-", 0)));
        var result = _parser.ParseBlocks("", template);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseBlocks_EmptyTemplate_ReturnsEmptyDict()
    {
        var result = _parser.ParseBlocks("file-name", FileNameTemplate.Empty);
        Assert.Empty(result);
    }

    #endregion

    #region Validate

    [Fact]
    public void Validate_NoBlocks_ReturnsFalse()
    {
        var (valid, error) = _parser.Validate("file", FileNameTemplate.Empty, []);
        Assert.False(valid);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Validate_EmptyFileName_ReturnsFalse()
    {
        var template = CreateTemplate((0, "f", DelimSeg("-", 0)));
        var (valid, _) = _parser.Validate("", template, []);
        Assert.False(valid);
    }

    [Fact]
    public void Validate_ValidSetup_ReturnsTrue()
    {
        var template = CreateTemplate((0, "f", DelimSeg("-", 0)));
        var (valid, _) = _parser.Validate("PRJ-S1", template, []);
        Assert.True(valid);
    }

    #endregion

    #region ValidateDetailed

    [Fact]
    public void ValidateDetailed_AllFieldsValid_ReturnsTrue()
    {
        var template = CreateTemplate(
            (0, "project", DelimSeg("-", 0)),
            (1, "status", DelimSeg("-", 0)));

        var library = new List<FieldDefinition>
        {
            new() { Name = "project", ValidationMode = ValidationMode.CharCount, MinLength = 2, MaxLength = 5 },
            new() { Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["S0", "S1"] }
        };

        var result = _parser.ValidateDetailed("PRJ-S1", template, library);

        Assert.True(result.IsValid);
        Assert.All(result.Blocks, b => Assert.True(b.IsValid));
    }

    [Fact]
    public void ValidateDetailed_AllowedValuesViolation_ReturnsFalse()
    {
        var template = CreateTemplate(
            (0, "project", DelimSeg("-", 0)),
            (1, "status", DelimSeg("-", 0)));

        var library = new List<FieldDefinition>
        {
            new() { Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["S0", "S1"] }
        };

        var result = _parser.ValidateDetailed("PRJ-S9", template, library);

        Assert.False(result.IsValid);
        Assert.Contains(result.Blocks, b => b.Field == "status" && !b.IsValid);
    }

    [Fact]
    public void ValidateDetailed_CharCountViolation_TooShort_ReturnsFalse()
    {
        var template = CreateTemplate((0, "code", DelimSeg("-", 0)));

        var library = new List<FieldDefinition>
        {
            new() { Name = "code", ValidationMode = ValidationMode.CharCount, MinLength = 4 }
        };

        var result = _parser.ValidateDetailed("AB-S1", template, library);

        Assert.False(result.IsValid);
        Assert.Contains(result.Blocks, b => !b.IsValid);
    }

    [Fact]
    public void ValidateDetailed_CharCountViolation_TooLong_ReturnsFalse()
    {
        var template = CreateTemplate((0, "code", DelimSeg("-", 0)));

        var library = new List<FieldDefinition>
        {
            new() { Name = "code", ValidationMode = ValidationMode.CharCount, MaxLength = 2 }
        };

        var result = _parser.ValidateDetailed("ABCDEF-S1", template, library);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateDetailed_NoValidationMode_AllValid()
    {
        var template = CreateTemplate((0, "code", DelimSeg("-", 0)));

        var library = new List<FieldDefinition>
        {
            new() { Name = "code", ValidationMode = ValidationMode.None }
        };

        var result = _parser.ValidateDetailed("anything-here", template, library);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateDetailed_NoFieldDef_InLibrary_StillValid()
    {
        var template = CreateTemplate((0, "unknown_field", DelimSeg("-", 0)));

        var result = _parser.ValidateDetailed("value-rest", template, []);

        Assert.True(result.IsValid);
    }

    #endregion

    #region TransformForExport

    [Fact]
    public void TransformForExport_AppliesMapping_ReturnsTransformed()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 0) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 0) },
                new() { Index = 2, Field = "type", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "S1", TargetValue = "S2" }
            }
        };

        var result = _parser.TransformForExport("PRJ-S1-DWG", template, []);

        Assert.Equal("PRJ-S2-DWG", result);
    }

    [Fact]
    public void TransformForExport_NoMapping_ReturnsSameBlocks()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 0) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = []
        };

        var result = _parser.TransformForExport("PRJ-S1", template, []);

        Assert.Equal("PRJ-S1", result);
    }

    [Fact]
    public void TransformForExport_MultipleMappings_AppliesAll()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 0) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 0) },
                new() { Index = 2, Field = "phase", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "WIP", TargetValue = "SHARED" },
                new() { Field = "phase", SourceValue = "P01", TargetValue = "P02" }
            }
        };

        var result = _parser.TransformForExport("ACME-WIP-P01", template, []);

        Assert.Equal("ACME-SHARED-P02", result);
    }

    [Fact]
    public void TransformForExport_SourceValueMismatch_NoChange()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "S1", TargetValue = "S2" }
            }
        };

        var result = _parser.TransformForExport("S9", template, []);

        Assert.Equal("S9", result);
    }

    [Fact]
    public void TransformForExport_EmptyFileName_ReturnsNull()
    {
        var result = _parser.TransformForExport("", new FileNameTemplate
        {
            Blocks = [new FileBlockDefinition { Index = 0, Field = "f", ParseRule = DelimSeg("-", 0) }]
        }, []);

        Assert.Null(result);
    }

    [Fact]
    public void TransformForExport_PreservesExtension()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "code", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = []
        };

        var result = _parser.TransformForExport("PRJ-S1.rvt", template, []);

        Assert.Equal("PRJ.rvt", result);
    }

    #endregion

    #region GetMappedValues

    [Fact]
    public void GetMappedValues_AppliesMatchingMapping()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 0) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "WIP", TargetValue = "SHARED" }
            }
        };

        var mapped = _parser.GetMappedValues("ACME-WIP", template);

        Assert.Equal("ACME", mapped["project"]);
        Assert.Equal("SHARED", mapped["status"]);
    }

    [Fact]
    public void GetMappedValues_NoMappings_ReturnsParsedValues()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 0) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = []
        };

        var mapped = _parser.GetMappedValues("ACME-WIP", template);

        Assert.Equal("ACME", mapped["project"]);
        Assert.Equal("WIP", mapped["status"]);
    }

    [Fact]
    public void GetMappedValues_SourceValueMismatch_NoChange()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "WIP", TargetValue = "SHARED" }
            }
        };

        var mapped = _parser.GetMappedValues("S1", template);

        Assert.Equal("S1", mapped["status"]);
    }

    #endregion

    #region ValidateExportMappings

    [Fact]
    public void ValidateExportMappings_MappedValueInvalid_ReturnsInvalid()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 0) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "WIP", TargetValue = "GARBAGE" }
            }
        };

        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["WIP", "SHARED"] }
        };

        var result = _parser.ValidateExportMappings("ACME-WIP", template, fieldLibrary);

        Assert.False(result.IsValid);
        Assert.Contains("GARBAGE", result.Summary);
    }

    [Fact]
    public void ValidateExportMappings_MappedValueValid_ReturnsValid()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 0) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "WIP", TargetValue = "SHARED" }
            }
        };

        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["WIP", "SHARED"] }
        };

        var result = _parser.ValidateExportMappings("ACME-WIP", template, fieldLibrary);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateExportMappings_CurrentValidButMappedInvalid_ReturnsInvalid()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "WIP", TargetValue = "XXX" }
            }
        };

        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["WIP", "SHARED"] }
        };

        var currentValidation = _parser.ValidateDetailed("WIP", template, fieldLibrary);
        Assert.True(currentValidation.IsValid);

        var exportValidation = _parser.ValidateExportMappings("WIP", template, fieldLibrary);
        Assert.False(exportValidation.IsValid);
    }

    [Fact]
    public void ValidateExportMappings_NoFieldDefinition_ReturnsValid()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "status", SourceValue = "WIP", TargetValue = "ANYTHING" }
            }
        };

        var result = _parser.ValidateExportMappings("WIP", template, []);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateExportMappings_CharCountValidation_WorksOnMappedValue()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "code", ParseRule = DelimSeg("-", 0) }
            },
            ExportMappings = new List<ExportMapping>
            {
                new() { Field = "code", SourceValue = "AB", TargetValue = "ABCDE" }
            }
        };

        var fieldLibrary = new List<FieldDefinition>
        {
            new() { Name = "code", ValidationMode = ValidationMode.CharCount, MaxLength = 3 }
        };

        var result = _parser.ValidateExportMappings("AB", template, fieldLibrary);

        Assert.False(result.IsValid);
    }

    #endregion

    #region ValidateSingleValue

    [Fact]
    public void ValidateSingleValue_AllowedValueValid_ReturnsTrue()
    {
        var fieldDef = new FieldDefinition
        {
            Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["WIP", "SHARED"]
        };

        var (valid, _) = _parser.ValidateSingleValue("WIP", fieldDef);

        Assert.True(valid);
    }

    [Fact]
    public void ValidateSingleValue_AllowedValueInvalid_ReturnsFalse()
    {
        var fieldDef = new FieldDefinition
        {
            Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["WIP", "SHARED"]
        };

        var (valid, error) = _parser.ValidateSingleValue("GARBAGE", fieldDef);

        Assert.False(valid);
        Assert.Contains("GARBAGE", error);
    }

    [Fact]
    public void ValidateSingleValue_CharCountTooLong_ReturnsFalse()
    {
        var fieldDef = new FieldDefinition
        {
            Name = "code", ValidationMode = ValidationMode.CharCount, MaxLength = 3
        };

        var (valid, error) = _parser.ValidateSingleValue("ABCDE", fieldDef);

        Assert.False(valid);
        Assert.Contains("too long", error);
    }

    [Fact]
    public void ValidateSingleValue_NoneValidation_ReturnsTrue()
    {
        var fieldDef = new FieldDefinition { Name = "free", ValidationMode = ValidationMode.None };

        var (valid, _) = _parser.ValidateSingleValue("anything", fieldDef);

        Assert.True(valid);
    }

    #endregion
}
