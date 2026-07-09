using System.Collections.Generic;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class ProjectBaseBindingEvaluatorTests
{
    private readonly ProjectBaseBindingEvaluator _evaluator = new(new FileNameParser());

    private static ProjectBaseBinding MakeBinding(params (string field, string delimiter, int segment)[] blocks)
    {
        var template = new FileNameTemplate
        {
            Blocks = blocks.Select((b, i) => new FileBlockDefinition
            {
                Index = i,
                Field = b.field,
                ParseRule = new ParseRule
                {
                    Mode = ParseMode.DelimiterSegment,
                    Delimiter = b.delimiter,
                    SegmentIndex = b.segment
                }
            }).ToList()
        };
        var library = blocks
            .Where(b => !string.IsNullOrEmpty(b.field))
            .Select(b => new FieldDefinition { Name = b.field })
            .ToList<FieldDefinition>();
        return new ProjectBaseBinding(template, library);
    }

    [Fact]
    public void Evaluate_NullBinding_ReturnsNotApplicable()
    {
        var result = _evaluator.Evaluate(null, "file.rvt");
        Assert.Equal(ProjectBaseMatchKind.NotApplicable, result.Kind);
    }

    [Fact]
    public void Evaluate_EmptyFilePath_ReturnsNotApplicable()
    {
        var binding = MakeBinding(("project", "-", 1));
        var result = _evaluator.Evaluate(binding, "");
        Assert.Equal(ProjectBaseMatchKind.NotApplicable, result.Kind);
    }

    [Fact]
    public void Evaluate_EmptyTemplate_ReturnsNotApplicable()
    {
        var binding = new ProjectBaseBinding(FileNameTemplate.Empty, new List<FieldDefinition>());
        var result = _evaluator.Evaluate(binding, "PRJ-S1.rvt");
        Assert.Equal(ProjectBaseMatchKind.NotApplicable, result.Kind);
    }

    [Fact]
    public void Evaluate_ValidFile_ReturnsMatch()
    {
        var binding = MakeBinding(
            ("project", "-", 1),
            ("status", "-", 1));
        var result = _evaluator.Evaluate(binding, "PRJ-S1.rvt");
        Assert.Equal(ProjectBaseMatchKind.Match, result.Kind);
        Assert.Null(result.Reason);
        Assert.Equal("PRJ", result.ParsedValues!["project"]);
        Assert.Equal("S1", result.ParsedValues["status"]);
    }

    [Fact]
    public void Evaluate_InvalidFile_ReturnsMismatchWithReason()
    {
        var template = new FileNameTemplate
        {
            Blocks =
            [
                new() { Index = 0, Field = "project", ParseRule = new ParseRule { Mode = ParseMode.DelimiterSegment, Delimiter = "-", SegmentIndex = 1 } },
                new() { Index = 1, Field = "status", ParseRule = new ParseRule { Mode = ParseMode.DelimiterSegment, Delimiter = "-", SegmentIndex = 1 } }
            ]
        };
        var library = new List<FieldDefinition>
        {
            new() { Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["S0", "S1"] }
        };
        var binding = new ProjectBaseBinding(template, library);
        var result = _evaluator.Evaluate(binding, "PRJ-S9.rvt");
        Assert.Equal(ProjectBaseMatchKind.Mismatch, result.Kind);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Evaluate_EmptyFieldInTemplate_ReturnsMismatch()
    {
        var template = new FileNameTemplate
        {
            Blocks =
            [
                new() { Index = 0, Field = string.Empty, ParseRule = new ParseRule { Mode = ParseMode.DelimiterSegment, Delimiter = "-", SegmentIndex = 1 } }
            ]
        };
        var binding = new ProjectBaseBinding(template, new List<FieldDefinition>());
        var result = _evaluator.Evaluate(binding, "PRJ-S1.rvt");
        Assert.Equal(ProjectBaseMatchKind.Mismatch, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public void Evaluate_FieldValidationFailure_ReturnsMismatch()
    {
        var template = new FileNameTemplate
        {
            Blocks =
            [
                new() { Index = 0, Field = "status", ParseRule = new ParseRule { Mode = ParseMode.DelimiterSegment, Delimiter = "-", SegmentIndex = 1 } }
            ]
        };
        var library = new List<FieldDefinition>
        {
            new() { Name = "status", ValidationMode = ValidationMode.AllowedValues, AllowedValues = ["S0", "S1"] }
        };
        var binding = new ProjectBaseBinding(template, library);
        var result = _evaluator.Evaluate(binding, "PRJ-S9.rvt");
        Assert.Equal(ProjectBaseMatchKind.Mismatch, result.Kind);
    }
}
