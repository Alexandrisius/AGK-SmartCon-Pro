using System.IO;
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

    private static ParseRule DelimSegMulti(string delimiter, int index, int count)
        => new() { Mode = ParseMode.DelimiterSegment, Delimiter = delimiter, SegmentIndex = index, SegmentCount = count };

    private static ParseRule FixedW(int count)
        => new() { Mode = ParseMode.FixedWidth, CharCount = count };

    private static ParseRule FixedW(int offset, int count)
        => new() { Mode = ParseMode.FixedWidth, CharOffset = offset, CharCount = count };

    private static ParseRule Between(string open, string close)
        => new() { Mode = ParseMode.BetweenMarkers, OpenMarker = open, CloseMarker = close };

    private static ParseRule Between(string open, string close, int openIdx, int closeIdx)
        => new() { Mode = ParseMode.BetweenMarkers, OpenMarker = open, CloseMarker = close, OpenMarkerIndex = openIdx, CloseMarkerIndex = closeIdx };

    private static ParseRule AfterM(string marker)
        => new() { Mode = ParseMode.AfterMarker, Marker = marker };

    private static ParseRule AfterM(string marker, int index)
        => new() { Mode = ParseMode.AfterMarker, Marker = marker, MarkerIndex = index };

    private static ParseRule RemainderRule()
        => new() { Mode = ParseMode.Remainder };

    #region ApplyParseRule — unit tests

    [Fact]
    public void ApplyParseRule_DelimiterSegment_TakesCorrectSegment()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", DelimSeg("-", 2));
        Assert.Equal("B", value);
        Assert.Equal("A-C", remaining);
    }

    [Fact]
    public void ApplyParseRule_DelimiterSegment_FirstSegment()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", DelimSeg("-", 1));
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
        var (value, remaining) = FileNameParser.ApplyParseRule("", DelimSeg("-", 1));
        Assert.Equal(string.Empty, value);
        Assert.Equal(string.Empty, remaining);
    }

    #endregion

    #region ParseBlocks — sequential consumptive

    [Fact]
    public void ParseBlocks_DelimiterSegments_ParsesAll()
    {
        var template = CreateTemplate(
            (0, "project", DelimSeg("-", 1)),
            (1, "status", DelimSeg("-", 1)),
            (2, "type", DelimSeg("-", 1)));

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
            (1, "status", DelimSeg("-", 2)));

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
        var template = CreateTemplate((0, "f", DelimSeg("-", 1)));
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
        var template = CreateTemplate((0, "f", DelimSeg("-", 1)));
        var (valid, _) = _parser.Validate("", template, []);
        Assert.False(valid);
    }

    [Fact]
    public void Validate_ValidSetup_ReturnsTrue()
    {
        var template = CreateTemplate((0, "f", DelimSeg("-", 1)));
        var (valid, _) = _parser.Validate("PRJ-S1", template, []);
        Assert.True(valid);
    }

    #endregion

    #region ValidateDetailed

    [Fact]
    public void ValidateDetailed_AllFieldsValid_ReturnsTrue()
    {
        var template = CreateTemplate(
            (0, "project", DelimSeg("-", 1)),
            (1, "status", DelimSeg("-", 1)));

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
            (0, "project", DelimSeg("-", 1)),
            (1, "status", DelimSeg("-", 1)));

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
        var template = CreateTemplate((0, "code", DelimSeg("-", 1)));

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
        var template = CreateTemplate((0, "code", DelimSeg("-", 1)));

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
        var template = CreateTemplate((0, "code", DelimSeg("-", 1)));

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
        var template = CreateTemplate((0, "unknown_field", DelimSeg("-", 1)));

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
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 1) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 1) },
                new() { Index = 2, Field = "type", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 1) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 1) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 1) },
                new() { Index = 2, Field = "phase", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 1) }
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
            Blocks = [new FileBlockDefinition { Index = 0, Field = "f", ParseRule = DelimSeg("-", 1) }]
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
                new() { Index = 0, Field = "code", ParseRule = DelimSeg("-", 1) }
            },
            ExportMappings = []
        };

        var result = _parser.TransformForExport("PRJ-S1.rvt", template, []);

        Assert.Equal("PRJ.rvt", result);
    }

    [Fact]
    public void TransformForExport_MultipleDotsInName_PreservesRealExtension()
    {
        var template = new FileNameTemplate
        {
            Blocks = new List<FileBlockDefinition>
            {
                new() { Index = 0, Field = "number", ParseRule = DelimSeg(".", 1) },
                new() { Index = 1, Field = "name", ParseRule = DelimSeg(".", 1) }
            },
            ExportMappings = []
        };

        var result = _parser.TransformForExport("01.Подвал.rvt", template, []);

        Assert.Equal("01-Подвал.rvt", result);
    }

    [Fact]
    public void ParseBlocks_MultipleDotsInName_SkipsRvtExtension()
    {
        var template = CreateTemplate(
            (0, "number", DelimSeg(".", 1)),
            (1, "name", DelimSeg(".", 1)));

        var result = _parser.ParseBlocks("01.Подвал.rvt", template);

        Assert.Equal("01", result["number"]);
        Assert.Equal("Подвал", result["name"]);
    }

    [Fact]
    public void ParseBlocks_FileNameWithDotButNoRvtExtension_ParsesCorrectly()
    {
        var template = CreateTemplate(
            (0, "number", DelimSeg(".", 1)),
            (1, "name", RemainderRule()));

        var result = _parser.ParseBlocks("01.Подвал.rvt", template);

        Assert.Equal("01", result["number"]);
        Assert.Equal("Подвал", result["name"]);
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
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 1) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 1) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 1) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("-", 1) },
                new() { Index = 1, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "status", ParseRule = DelimSeg("-", 1) }
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
                new() { Index = 0, Field = "code", ParseRule = DelimSeg("-", 1) }
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

    #region Issue #18 — worksharing local file with username suffix

    [Fact]
    public void ParseBlocks_DocTitle_WithoutRvt_DropsUsernameAsExtension()
    {
        var template = CreateTemplate(
            (0, "project", DelimSeg("_", 1)),
            (1, "user", DelimSeg(".", 2)));

        var docTitle = "Project1_a.matorin";

        var remaining = Path.GetFileNameWithoutExtension(docTitle);
        Assert.Equal("Project1_a", remaining);

        var parsed = _parser.ParseBlocks(docTitle, template);

        Assert.Equal("Project1", parsed["project"]);
        Assert.Equal(string.Empty, parsed["user"]);
    }

    [Fact]
    public void ParseBlocks_FullFileName_WithRvt_ParsesUsernameCorrectly()
    {
        var template = CreateTemplate(
            (0, "project", DelimSeg("_", 1)),
            (1, "user", DelimSeg(".", 2)));

        var fullFileName = "Project1_a.matorin.rvt";

        var remaining = Path.GetFileNameWithoutExtension(fullFileName);
        Assert.Equal("Project1_a.matorin", remaining);

        var parsed = _parser.ParseBlocks(fullFileName, template);

        Assert.Equal("Project1", parsed["project"]);
        Assert.Equal("matorin", parsed["user"]);
    }

    [Fact]
    public void TransformForExport_DocTitle_WithoutRvt_UsernameAppearsAsFakeExtension()
    {
        var template = new FileNameTemplate
        {
            Blocks =
            [
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("_", 1) },
                new() { Index = 1, Field = "user", ParseRule = DelimSeg(".", 1) }
            ],
            ExportMappings = []
        };

        var docTitle = "Project1_a.matorin";

        var result = _parser.TransformForExport(docTitle, template, []);

        Assert.Equal("Project1-a.matorin", result);
    }

    [Fact]
    public void TransformForExport_FullFileName_WithRvt_CorrectExport()
    {
        var template = new FileNameTemplate
        {
            Blocks =
            [
                new() { Index = 0, Field = "project", ParseRule = DelimSeg("_", 1) },
                new() { Index = 1, Field = "user", ParseRule = DelimSeg(".", 2) }
            ],
            ExportMappings = []
        };

        var fullFileName = "Project1_a.matorin.rvt";

        var result = _parser.TransformForExport(fullFileName, template, []);

        Assert.Equal("Project1-matorin.rvt", result);
    }

    [Fact]
    public void ParseBlocks_WorksharingLocalFile_FullPath_ParsesAllFields()
    {
        var template = CreateTemplate(
            (0, "code", DelimSeg("-", 1)),
            (1, "discipline", DelimSeg("-", 1)),
            (2, "phase", DelimSeg("-", 1)),
            (3, "status", DelimSeg("_", 1)),
            (4, "user", DelimSeg(".", 2)));

        var fullFileName = "1001-AR-P-S1_a.klimovich.rvt";

        var parsed = _parser.ParseBlocks(fullFileName, template);

        Assert.Equal("1001", parsed["code"]);
        Assert.Equal("AR", parsed["discipline"]);
        Assert.Equal("P", parsed["phase"]);
        Assert.Equal("S1", parsed["status"]);
        Assert.Equal("klimovich", parsed["user"]);
    }

    #endregion

    #region SegmentCount — multi-segment extraction

    [Fact]
    public void ApplyParseRule_DelimiterSegment_MultiSegment_ExtractsTwoConsecutive()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("12-59-Сарай", DelimSegMulti("-", 1, 2));
        Assert.Equal("12-59", value);
        Assert.Equal("Сарай", remaining);
    }

    [Fact]
    public void ApplyParseRule_DelimiterSegment_MultiSegment_MiddleRange()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C-D-E", DelimSegMulti("-", 2, 2));
        Assert.Equal("B-C", value);
        Assert.Equal("A-D-E", remaining);
    }

    [Fact]
    public void ApplyParseRule_DelimiterSegment_MultiSegment_CountExceedsAvailable()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", DelimSegMulti("-", 2, 10));
        Assert.Equal("B-C", value);
        Assert.Equal("A", remaining);
    }

    [Fact]
    public void ApplyParseRule_DelimiterSegment_SegmentCountZero_TreatedAsOne()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", DelimSegMulti("-", 1, 0));
        Assert.Equal("A", value);
        Assert.Equal("B-C", remaining);
    }

    [Fact]
    public void ApplyParseRule_DelimiterSegment_SegmentCountNegative_TreatedAsOne()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", DelimSegMulti("-", 2, -5));
        Assert.Equal("B", value);
        Assert.Equal("A-C", remaining);
    }

    [Fact]
    public void ParseBlocks_MultiSegmentBlock_ConsumptiveParsing()
    {
        var template = CreateTemplate(
            (0, "code", DelimSegMulti("-", 1, 2)),
            (1, "name", RemainderRule()));

        var result = _parser.ParseBlocks("12-59-Сарай.rvt", template);

        Assert.Equal("12-59", result["code"]);
        Assert.Equal("Сарай", result["name"]);
    }

    [Fact]
    public void ParseBlocks_MultiSegmentBlock_ThreePartName()
    {
        var template = CreateTemplate(
            (0, "prefix", DelimSegMulti("-", 1, 2)),
            (1, "suffix", DelimSegMulti("-", 1, 2)));

        var result = _parser.ParseBlocks("X-Y-Z-W.rvt", template);

        Assert.Equal("X-Y", result["prefix"]);
        Assert.Equal("Z-W", result["suffix"]);
    }

    [Fact]
    public void TransformForExport_MultiSegmentBlock_ReconstructsCorrectly()
    {
        var template = CreateTemplate(
            (0, "code", DelimSegMulti("-", 1, 2)),
            (1, "name", DelimSeg("-", 1)));

        var result = _parser.TransformForExport("12-59-Сарай.rvt", template, []);

        Assert.Equal("12-59-Сарай.rvt", result);
    }

    #endregion

    #region BetweenMarkers — OpenMarkerIndex (1-based)

    [Fact]
    public void ApplyParseRule_BetweenMarkers_SecondOpenMarker_ExtractsSecondGroup()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ(S1)(S2)-001", Between("(", ")", 2, 2));
        Assert.Equal("S2", value);
        Assert.Equal("PRJ(S1)-001", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_ThirdOpenMarker_ExtractsThirdGroup()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("(A)(B)(C)", Between("(", ")", 3, 3));
        Assert.Equal("C", value);
        Assert.Equal("(A)(B)", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_OpenIndexOutOfRange_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("(A)(B)", Between("(", ")", 6, 1));
        Assert.Equal(string.Empty, value);
        Assert.Equal("(A)(B)", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_DefaultOneIndex_FirstOccurrence()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ(S1)-001", Between("(", ")", 1, 1));
        Assert.Equal("S1", value);
        Assert.Equal("PRJ-001", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_DefaultProperties_FirstPair()
    {
        var rule = new ParseRule { Mode = ParseMode.BetweenMarkers, OpenMarker = "(", CloseMarker = ")" };
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ(S1)-001", rule);
        Assert.Equal("S1", value);
        Assert.Equal("PRJ-001", remaining);
    }

    #endregion

    #region BetweenMarkers — CloseMarkerIndex absolute (1-based)

    [Fact]
    public void ApplyParseRule_BetweenMarkers_AbsoluteSecondClose_SkipsInnerClose()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A(B)(C)D", Between("(", ")", 1, 2));
        Assert.Equal("B)(C", value);
        Assert.Equal("AD", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_CloseIndexOutOfRange_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A(B)C", Between("(", ")", 1, 6));
        Assert.Equal(string.Empty, value);
        Assert.Equal("A(B)C", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_CloseBeforeOpen_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("(A)(B)(C)D", Between("(", ")", 2, 1));
        Assert.Equal(string.Empty, value);
        Assert.Equal("(A)(B)(C)D", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_SameSymbolDifferentOccurrences()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A_B_C_D", Between("_", "_", 1, 2));
        Assert.Equal("B", value);
        Assert.Equal("AC_D", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_SameSymbolSameOccurrence_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A_B_C", Between("_", "_", 1, 1));
        Assert.Equal(string.Empty, value);
        Assert.Equal("A_B_C", remaining);
    }

    [Fact]
    public void ApplyParseRule_BetweenMarkers_SameSymbolWideRange()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A_B_C_D", Between("_", "_", 1, 3));
        Assert.Equal("B_C", value);
        Assert.Equal("AD", remaining);
    }

    #endregion

    #region AfterMarker — MarkerIndex (1-based)

    [Fact]
    public void ApplyParseRule_AfterMarker_SecondOccurrence()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C-D", AfterM("-", 2));
        Assert.Equal("C-D", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_AfterMarker_ThirdOccurrence()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C-D", AfterM("-", 3));
        Assert.Equal("D", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_AfterMarker_IndexOutOfRange_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", AfterM("-", 6));
        Assert.Equal(string.Empty, value);
        Assert.Equal("A-B-C", remaining);
    }

    [Fact]
    public void ApplyParseRule_AfterMarker_DefaultOneIndex_FirstOccurrence()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ-S1-DWG", AfterM("-S1-", 1));
        Assert.Equal("DWG", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_AfterMarker_MultiCharMarker_ThirdOcc()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("##A##B##C", AfterM("##", 3));
        Assert.Equal("C", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_AfterMarker_DefaultProperties_FirstOccurrence()
    {
        var rule = new ParseRule { Mode = ParseMode.AfterMarker, Marker = "-" };
        var (value, remaining) = FileNameParser.ApplyParseRule("A-B-C", rule);
        Assert.Equal("B-C", value);
        Assert.Equal(string.Empty, remaining);
    }

    #endregion

    #region FixedWidth — CharOffset

    [Fact]
    public void ApplyParseRule_FixedWidth_WithOffset_ExtractsMiddle()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ1234-S1", FixedW(3, 4));
        Assert.Equal("1234", value);
        Assert.Equal("-S1", remaining);
    }

    [Fact]
    public void ApplyParseRule_FixedWidth_OffsetExceedsLength_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("AB", FixedW(5, 2));
        Assert.Equal(string.Empty, value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_FixedWidth_OffsetPlusCountExceedsLength_TakesAllAfterOffset()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("ABCD", FixedW(2, 10));
        Assert.Equal("CD", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_FixedWidth_OffsetZeroCountZero_ReturnsEmpty()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("ABCD", FixedW(0, 0));
        Assert.Equal(string.Empty, value);
        Assert.Equal("ABCD", remaining);
    }

    [Fact]
    public void ApplyParseRule_FixedWidth_OffsetOnly_SkipsPrefix()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PREFIX-data", FixedW(7, 4));
        Assert.Equal("data", value);
        Assert.Equal(string.Empty, remaining);
    }

    [Fact]
    public void ApplyParseRule_FixedWidth_DefaultOffsetZero_SameAsOldBehavior()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("PRJ-S1", FixedW(0, 3));
        Assert.Equal("PRJ", value);
        Assert.Equal("-S1", remaining);
    }

    #endregion

    #region Consumptive chains with new parameters

    [Fact]
    public void ParseBlocks_FixedWidthWithOffset_ThenDelimiter()
    {
        var template = CreateTemplate(
            (0, "code", FixedW(3, 4)),
            (1, "suffix", DelimSeg("-", 2)));

        var result = _parser.ParseBlocks("PRJ1234-S1.rvt", template);

        Assert.Equal("1234", result["code"]);
        Assert.Equal("S1", result["suffix"]);
    }

    [Fact]
    public void ParseBlocks_BetweenMarkersSecondOcc_ThenRemainder()
    {
        var template = CreateTemplate(
            (0, "value", Between("(", ")", 2, 2)),
            (1, "rest", RemainderRule()));

        var result = _parser.ParseBlocks("(A)(B)(C).rvt", template);

        Assert.Equal("B", result["value"]);
        Assert.Equal("(A)(C)", result["rest"]);
    }

    [Fact]
    public void ParseBlocks_AfterMarkerSecondOcc_ConsumptiveChain()
    {
        var template = CreateTemplate(
            (0, "first", DelimSeg("-", 1)),
            (1, "after2nd", AfterM("-", 2)));

        var result = _parser.ParseBlocks("A-B-C-D.rvt", template);

        Assert.Equal("A", result["first"]);
        Assert.Equal("D", result["after2nd"]);
    }

    [Fact]
    public void ParseBlocks_FixedWidthOffset_ThenBetweenMarkers_ThenRemainder()
    {
        var template = CreateTemplate(
            (0, "skip", FixedW(3, 2)),
            (1, "inner", Between("[", "]", 1, 1)),
            (2, "rest", RemainderRule()));

        var result = _parser.ParseBlocks("XXXAB[CD]EF.rvt", template);

        Assert.Equal("AB", result["skip"]);
        Assert.Equal("CD", result["inner"]);
        Assert.Equal("EF", result["rest"]);
    }

    [Fact]
    public void TransformForExport_BetweenMarkersSecondOcc_PreservesCorrectly()
    {
        var template = CreateTemplate(
            (0, "first", Between("(", ")", 1, 1)),
            (1, "second", Between("(", ")", 1, 1)));

        var result = _parser.TransformForExport("(A)(B).rvt", template, []);

        Assert.Equal("A-B.rvt", result);
    }

    [Fact]
    public void ParseBlocks_RealWorld_SmartConExtraction()
    {
        var template = CreateTemplate(
            (0, "module", DelimSeg("_", 2)),
            (1, "year", DelimSeg("_", 2)));

        var result = _parser.ParseBlocks("Тестовый_SmartCon_2025.rvt", template);

        Assert.Equal("SmartCon", result["module"]);
        Assert.Equal("2025", result["year"]);
    }

    [Fact]
    public void ParseBlocks_BetweenSameSymbol_ExtractsBetween()
    {
        var (value, remaining) = FileNameParser.ApplyParseRule("A_B_C_D", Between("_", "_", 1, 2));
        Assert.Equal("B", value);
        Assert.Equal("AC_D", remaining);
    }

    #endregion
}
