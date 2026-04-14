using SmartCon.Core.Math;
using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class LookupTableCsvParserTests
{
    // ── TryParseRevitValue ──────────────────────────────────────────────

    [Fact]
    public void TryParseRevitValue_PlainNumber_ReturnsTrue()
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue("15.0", out double v));
        Assert.Equal(15.0, v);
    }

    [Fact]
    public void TryParseRevitValue_IntegerString_ReturnsTrue()
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue("20", out double v));
        Assert.Equal(20.0, v);
    }

    [Fact]
    public void TryParseRevitValue_WithMmSuffix_ReturnsTrue()
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue("25 mm", out double v));
        Assert.Equal(25.0, v);
    }

    [Fact]
    public void TryParseRevitValue_WithMmNoSpace_ReturnsTrue()
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue("32mm", out double v));
        Assert.Equal(32.0, v);
    }

    [Fact]
    public void TryParseRevitValue_WithFtSuffix_ReturnsTrue()
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue("1.5 ft", out double v));
        Assert.Equal(1.5, v);
    }

    [Fact]
    public void TryParseRevitValue_WithInSuffix_ReturnsTrue()
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue("0.5 in", out double v));
        Assert.Equal(0.5, v);
    }

    [Fact]
    public void TryParseRevitValue_WithQuotes_ReturnsTrue()
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue("\"15.0\"", out double v));
        Assert.Equal(15.0, v);
    }

    [Fact]
    public void TryParseRevitValue_EmptyString_ReturnsFalse()
    {
        Assert.False(LookupTableCsvParser.TryParseRevitValue("", out _));
    }

    [Fact]
    public void TryParseRevitValue_Whitespace_ReturnsFalse()
    {
        Assert.False(LookupTableCsvParser.TryParseRevitValue("   ", out _));
    }

    [Fact]
    public void TryParseRevitValue_NonNumeric_ReturnsFalse()
    {
        Assert.False(LookupTableCsvParser.TryParseRevitValue("abc", out _));
    }

    [Fact]
    public void TryParseRevitValue_Null_ReturnsFalse()
    {
        Assert.False(LookupTableCsvParser.TryParseRevitValue(null!, out _));
    }

    [Theory]
    [InlineData("15", 15.0)]
    [InlineData("20.0", 20.0)]
    [InlineData("25 mm", 25.0)]
    [InlineData("32mm", 32.0)]
    [InlineData("1.5 ft", 1.5)]
    [InlineData("\"40\"", 40.0)]
    public void TryParseRevitValue_ValidInputs_ParseCorrectly(string input, double expected)
    {
        Assert.True(LookupTableCsvParser.TryParseRevitValue(input, out double v));
        Assert.Equal(expected, v, precision: 3);
    }

    // ── ExtractColumnValues: basic CSV ─────────────────────────────────

    [Fact]
    public void ExtractColumnValues_EmptyCsv_ReturnsEmptyList()
    {
        var result = LookupTableCsvParser.ExtractColumnValues(
            [], 1, [], null);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractColumnValues_HeaderOnly_ReturnsEmptyList()
    {
        var csv = new[] { "A,B,C" };
        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, [], null);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractColumnValues_SingleDataRow_ExtractsColumn()
    {
        var csv = new[] { "Name,DN,Other", "Item1,15.0,X" };
        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, [], null);
        Assert.Single(result);
        Assert.Equal(15.0, result[0]);
    }

    [Fact]
    public void ExtractColumnValues_MultipleRows_ExtractsAll()
    {
        var csv = new[]
        {
            "Name,DN,Other",
            "A,15.0,X",
            "B,20.0,Y",
            "C,25.0,Z",
        };
        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, [], null);
        Assert.Equal(3, result.Count);
        Assert.Equal(15.0, result[0]);
        Assert.Equal(20.0, result[1]);
        Assert.Equal(25.0, result[2]);
    }

    [Fact]
    public void ExtractColumnValues_SkipsBlankLines()
    {
        var csv = new[]
        {
            "Name,DN,Other",
            "A,15.0,X",
            "",
            "B,20.0,Y",
        };
        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, [], null);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExtractColumnValues_SkipsNonNumeric()
    {
        var csv = new[]
        {
            "Name,DN,Other",
            "A,15.0,X",
            "B,text,Y",
            "C,25.0,Z",
        };
        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, [], null);
        Assert.Equal(2, result.Count);
        Assert.Equal(15.0, result[0]);
        Assert.Equal(25.0, result[1]);
    }

    [Fact]
    public void ExtractColumnValues_ColumnIndexOutOfRange_SkipsRow()
    {
        var csv = new[]
        {
            "Name,DN",
            "A,15.0",
        };
        var result = LookupTableCsvParser.ExtractColumnValues(csv, 5, [], null);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractColumnValues_QuotedCell_ParsesValue()
    {
        var csv = new[]
        {
            "Name,DN",
            "A,\"20.0\"",
        };
        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, [], null);
        Assert.Single(result);
        Assert.Equal(20.0, result[0]);
    }

    // ── ExtractColumnValues: constraint filtering ──────────────────────

    [Fact]
    public void ExtractColumnValues_WithConstraint_FiltersRows()
    {
        var csv = new[]
        {
            "Name,DN1,DN2,Code",
            "A,15,20,X",
            "B,15,25,Y",
            "C,20,25,Z",
        };

        var columns = new CsvColumnMapping[]
        {
            new(1, "DN1"),
            new(2, "DN2"),
        };

        var constraints = new LookupColumnConstraint[]
        {
            new(ConnectorIndex: 2, ParameterName: "DN2", ValueMm: 25.0),
        };

        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, columns, constraints);
        Assert.Equal(2, result.Count);
        Assert.Equal(15.0, result[0]);
        Assert.Equal(20.0, result[1]);
    }

    [Fact]
    public void ExtractColumnValues_WithConstraint_AllRowsFilteredOut_ReturnsEmpty()
    {
        var csv = new[]
        {
            "Name,DN1,DN2,Code",
            "A,15,20,X",
            "B,20,30,Y",
        };

        var columns = new CsvColumnMapping[]
        {
            new(1, "DN1"),
            new(2, "DN2"),
        };

        var constraints = new LookupColumnConstraint[]
        {
            new(ConnectorIndex: 2, ParameterName: "DN2", ValueMm: 99.0),
        };

        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, columns, constraints);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractColumnValues_SingleQueryColumn_NoConstraints_NoFilter()
    {
        var csv = new[]
        {
            "Name,DN",
            "A,15",
            "B,20",
        };

        var columns = new CsvColumnMapping[]
        {
            new(1, "DN"),
        };

        var result = LookupTableCsvParser.ExtractColumnValues(csv, 1, columns, null);
        Assert.Equal(2, result.Count);
    }

    // ── ApplyConstraintFilter ──────────────────────────────────────────

    [Fact]
    public void ApplyConstraintFilter_MatchingConstraint_PassesRow()
    {
        var cols = new[] { "A", "15", "25", "X" };
        var columns = new CsvColumnMapping[]
        {
            new(1, "DN1"),
            new(2, "DN2"),
        };
        var constraints = new LookupColumnConstraint[]
        {
            new(0, "DN2", 25.0),
        };
        int filtered = 0;

        bool result = LookupTableCsvParser.ApplyConstraintFilter(cols, 1, columns, constraints, 0, ref filtered);
        Assert.True(result);
        Assert.Equal(0, filtered);
    }

    [Fact]
    public void ApplyConstraintFilter_NonMatchingConstraint_FiltersRow()
    {
        var cols = new[] { "A", "15", "20", "X" };
        var columns = new CsvColumnMapping[]
        {
            new(1, "DN1"),
            new(2, "DN2"),
        };
        var constraints = new LookupColumnConstraint[]
        {
            new(0, "DN2", 99.0),
        };
        int filtered = 0;

        bool result = LookupTableCsvParser.ApplyConstraintFilter(cols, 1, columns, constraints, 0, ref filtered);
        Assert.False(result);
        Assert.Equal(1, filtered);
    }

    [Fact]
    public void ApplyConstraintFilter_TargetColumnSameAsConstraint_SkipsCheck()
    {
        var cols = new[] { "A", "15", "25", "X" };
        var columns = new CsvColumnMapping[]
        {
            new(1, "DN1"),
            new(2, "DN2"),
        };
        var constraints = new LookupColumnConstraint[]
        {
            new(0, "DN1", 15.0),
        };
        int filtered = 0;

        bool result = LookupTableCsvParser.ApplyConstraintFilter(cols, 1, columns, constraints, 0, ref filtered);
        Assert.True(result);
    }

    [Fact]
    public void ApplyConstraintFilter_ConstraintColumnOutOfRange_FiltersRow()
    {
        var cols = new[] { "A", "15" };
        var columns = new CsvColumnMapping[]
        {
            new(1, "DN1"),
            new(5, "DN2"),
        };
        var constraints = new LookupColumnConstraint[]
        {
            new(0, "DN2", 25.0),
        };
        int filtered = 0;

        bool result = LookupTableCsvParser.ApplyConstraintFilter(cols, 1, columns, constraints, 0, ref filtered);
        Assert.False(result);
    }
}
