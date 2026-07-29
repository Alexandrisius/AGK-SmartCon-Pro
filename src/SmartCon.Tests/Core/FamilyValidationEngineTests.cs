using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core;

public sealed class FamilyValidationEngineTests
{
    private readonly FamilyValidationEngine _engine = new();

    private static ParameterValidationValue TextParam(string name, string? value) =>
        new(name, true, value, null, null);

    private static ParameterValidationValue NumberParam(string name, double? value) =>
        new(name, true, null, value, null);

    private static ParameterValidationValue AbsentParam(string name) =>
        new(name, false, null, null, null);

    private static FamilyValidationInput InputWith(params ParameterValidationValue[] values) =>
        new([new FamilyTypeValidationData("TypeA", values)]);

    private static FamilyValidationInput TwoTypeInput(
        ParameterValidationValue[] typeAValues,
        ParameterValidationValue[] typeBValues) =>
        new([
            new FamilyTypeValidationData("TypeA", typeAValues),
            new FamilyTypeValidationData("TypeB", typeBValues),
        ]);

    private static EffectiveValidationRule Rule(
        string attribute,
        ValidationRuleOperator op,
        string? valueText = null,
        double? valueNumber = null,
        double? min = null,
        double? max = null) =>
        new(attribute, false, new ValidationRule("r1", "b1", op, valueText, valueNumber, min, max, null, 0, true));

    [Fact]
    public void Validate_NoRules_ReturnsValid()
    {
        var report = _engine.Validate(InputWith(TextParam("P", "x")), []);

        Assert.True(report.IsValid);
        Assert.Empty(report.Violations);
        Assert.Equal(0, report.RulesEvaluated);
    }

    [Fact]
    public void Validate_DisabledRule_Skipped()
    {
        var rule = Rule("P", ValidationRuleOperator.HasValue);
        rule = rule with { Rule = rule.Rule with { IsEnabled = false } };

        var report = _engine.Validate(InputWith(AbsentParam("P")), [rule]);

        Assert.True(report.IsValid);
        Assert.Equal(0, report.RulesEvaluated);
    }

    [Fact]
    public void Validate_NoTypes_ReturnsValid()
    {
        var report = _engine.Validate(new FamilyValidationInput([]), [Rule("P", ValidationRuleOperator.HasValue)]);

        Assert.True(report.IsValid);
    }

    [Theory]
    [InlineData("Pressure")]
    [InlineData("PRESSURE")]
    [InlineData("pressure")]
    public void IsPresent_ParameterNameMatch_CaseInsensitive(string ruleName)
    {
        var report = _engine.Validate(
            InputWith(TextParam("Pressure", "16")),
            [Rule(ruleName, ValidationRuleOperator.IsPresent)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void IsPresent_ParameterAbsent_Violation()
    {
        var report = _engine.Validate(InputWith(), [Rule("Pressure", ValidationRuleOperator.IsPresent)]);

        Assert.False(report.IsValid);
        var v = Assert.Single(report.Violations);
        Assert.Equal("TypeA", v.TypeName);
        Assert.Equal("Pressure", v.AttributeName);
        Assert.Equal(ValidationRuleOperator.IsPresent, v.Operator);
        Assert.Null(v.ActualValue);
    }

    [Fact]
    public void IsPresent_EmptyValue_StillPresent()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Pressure", null)),
            [Rule("Pressure", ValidationRuleOperator.IsPresent)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void HasValue_TextFilled_Passes()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Manufacturer", "AGK")),
            [Rule("Manufacturer", ValidationRuleOperator.HasValue)]);

        Assert.True(report.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasValue_TextEmpty_Violation(string? text)
    {
        var report = _engine.Validate(
            InputWith(TextParam("Manufacturer", text)),
            [Rule("Manufacturer", ValidationRuleOperator.HasValue)]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void HasValue_ParameterAbsent_Violation()
    {
        var report = _engine.Validate(InputWith(), [Rule("Manufacturer", ValidationRuleOperator.HasValue)]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void HasValue_NumberZero_Passes()
    {
        var report = _engine.Validate(
            InputWith(NumberParam("Offset", 0.0)),
            [Rule("Offset", ValidationRuleOperator.HasValue)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void IsEmpty_EmptyText_Passes()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Comment", "  ")),
            [Rule("Comment", ValidationRuleOperator.IsEmpty)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void IsEmpty_ParameterAbsent_Passes()
    {
        var report = _engine.Validate(InputWith(), [Rule("Comment", ValidationRuleOperator.IsEmpty)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void IsEmpty_FilledValue_Violation()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Comment", "note")),
            [Rule("Comment", ValidationRuleOperator.IsEmpty)]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void Equals_Text_IgnoreCase_Passes()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Material", "Сталь")),
            [Rule("Material", ValidationRuleOperator.Equals, valueText: "СТАЛЬ")]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void Equals_TextMismatch_Violation()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Material", "Медь")),
            [Rule("Material", ValidationRuleOperator.Equals, valueText: "Сталь")]);

        Assert.False(report.IsValid);
        var v = Assert.Single(report.Violations);
        Assert.Equal("Сталь", v.ExpectedValue);
        Assert.Equal("Медь", v.ActualValue);
    }

    [Fact]
    public void Equals_ParameterAbsent_Violation()
    {
        var report = _engine.Validate(InputWith(), [Rule("Material", ValidationRuleOperator.Equals, valueText: "Сталь")]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void Equals_Number_WithEpsilon_Passes()
    {
        var report = _engine.Validate(
            InputWith(NumberParam("DN", 50.0 + 1e-12)),
            [Rule("DN", ValidationRuleOperator.Equals, valueNumber: 50.0)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void Equals_Number_Mismatch_Violation()
    {
        var report = _engine.Validate(
            InputWith(NumberParam("DN", 65.0)),
            [Rule("DN", ValidationRuleOperator.Equals, valueNumber: 50.0)]);

        Assert.False(report.IsValid);
        var v = Assert.Single(report.Violations);
        Assert.Equal(50.0.ToString("G17", System.Globalization.CultureInfo.InvariantCulture), v.ExpectedValue);
    }

    [Fact]
    public void NotEquals_DifferentText_Passes()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Material", "Медь")),
            [Rule("Material", ValidationRuleOperator.NotEquals, valueText: "Сталь")]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void NotEquals_SameText_Violation()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Material", "Сталь")),
            [Rule("Material", ValidationRuleOperator.NotEquals, valueText: "Сталь")]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void NotEquals_ParameterAbsent_Passes()
    {
        var report = _engine.Validate(InputWith(), [Rule("Material", ValidationRuleOperator.NotEquals, valueText: "Сталь")]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void Contains_Substring_IgnoreCase_Passes()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Name", "Отвод AGK 90 градусов")),
            [Rule("Name", ValidationRuleOperator.Contains, valueText: "agk")]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void Contains_NoSubstring_Violation()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Name", "Отвод 90 градусов")),
            [Rule("Name", ValidationRuleOperator.Contains, valueText: "AGK")]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void Contains_ParameterAbsent_Violation()
    {
        var report = _engine.Validate(InputWith(), [Rule("Name", ValidationRuleOperator.Contains, valueText: "AGK")]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void NotContains_NoSubstring_Passes()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Name", "Отвод 90")),
            [Rule("Name", ValidationRuleOperator.NotContains, valueText: "temp")]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void NotContains_HasSubstring_Violation()
    {
        var report = _engine.Validate(
            InputWith(TextParam("Name", "temp_Отвод")),
            [Rule("Name", ValidationRuleOperator.NotContains, valueText: "TEMP")]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void NotContains_ParameterAbsent_Passes()
    {
        var report = _engine.Validate(InputWith(), [Rule("Name", ValidationRuleOperator.NotContains, valueText: "temp")]);

        Assert.True(report.IsValid);
    }

    [Theory]
    [InlineData(10.1, true)]
    [InlineData(10.0, false)]
    [InlineData(9.9, false)]
    public void GreaterThan_Boundary(double actual, bool expectedValid)
    {
        var report = _engine.Validate(
            InputWith(NumberParam("Pressure", actual)),
            [Rule("Pressure", ValidationRuleOperator.GreaterThan, valueNumber: 10.0)]);

        Assert.Equal(expectedValid, report.IsValid);
    }

    [Theory]
    [InlineData(10.1, true)]
    [InlineData(10.0, true)]
    [InlineData(9.9, false)]
    public void GreaterOrEqual_Boundary(double actual, bool expectedValid)
    {
        var report = _engine.Validate(
            InputWith(NumberParam("Pressure", actual)),
            [Rule("Pressure", ValidationRuleOperator.GreaterOrEqual, valueNumber: 10.0)]);

        Assert.Equal(expectedValid, report.IsValid);
    }

    [Theory]
    [InlineData(9.9, true)]
    [InlineData(10.0, false)]
    [InlineData(10.1, false)]
    public void LessThan_Boundary(double actual, bool expectedValid)
    {
        var report = _engine.Validate(
            InputWith(NumberParam("Pressure", actual)),
            [Rule("Pressure", ValidationRuleOperator.LessThan, valueNumber: 10.0)]);

        Assert.Equal(expectedValid, report.IsValid);
    }

    [Theory]
    [InlineData(9.9, true)]
    [InlineData(10.0, true)]
    [InlineData(10.1, false)]
    public void LessOrEqual_Boundary(double actual, bool expectedValid)
    {
        var report = _engine.Validate(
            InputWith(NumberParam("Pressure", actual)),
            [Rule("Pressure", ValidationRuleOperator.LessOrEqual, valueNumber: 10.0)]);

        Assert.Equal(expectedValid, report.IsValid);
    }

    [Theory]
    [InlineData(15.0, true)]
    [InlineData(100.0, true)]
    [InlineData(50.0, true)]
    [InlineData(14.9, false)]
    [InlineData(100.1, false)]
    public void Between_Boundary(double actual, bool expectedValid)
    {
        var report = _engine.Validate(
            InputWith(NumberParam("DN", actual)),
            [Rule("DN", ValidationRuleOperator.Between, min: 15.0, max: 100.0)]);

        Assert.Equal(expectedValid, report.IsValid);
    }

    [Fact]
    public void NumericRule_DisplayNumberPreferredOverInternal()
    {
        // internal feet 0.328 ft vs display 100 mm: the rule authored in
        // display units (15–100) must compare against 100, not 0.328.
        var report = _engine.Validate(
            new FamilyValidationInput([new FamilyTypeValidationData("T",
                new ParameterValidationValue[] { new("DN", true, "100 мм", 0.328, "unit:mm", DisplayNumber: 100.0) })]),
            [Rule("DN", ValidationRuleOperator.Between, min: 15.0, max: 100.0)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void NumericRule_NoDisplayNumber_FallsBackToInternal()
    {
        var report = _engine.Validate(
            InputWith(NumberParam("DN", 50.0)),
            [Rule("DN", ValidationRuleOperator.GreaterThan, valueNumber: 10.0)]);

        Assert.True(report.IsValid);
    }

    [Fact]
    public void Between_Violation_CarriesMinMax()
    {
        var report = _engine.Validate(
            InputWith(NumberParam("DN", 125.0)),
            [Rule("DN", ValidationRuleOperator.Between, min: 15.0, max: 100.0)]);

        var v = Assert.Single(report.Violations);
        Assert.Equal(15.0, v.ExpectedMin);
        Assert.Equal(100.0, v.ExpectedMax);
    }

    [Fact]
    public void NumericRule_TextValue_Violation()
    {
        var report = _engine.Validate(
            InputWith(TextParam("DN", "пятьдесят")),
            [Rule("DN", ValidationRuleOperator.GreaterThan, valueNumber: 10.0)]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void NumericRule_ParameterAbsent_Violation()
    {
        var report = _engine.Validate(InputWith(), [Rule("DN", ValidationRuleOperator.GreaterThan, valueNumber: 10.0)]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void Equals_Number_BeyondEpsilon_Violation()
    {
        var report = _engine.Validate(
            InputWith(NumberParam("DN", 50.0 + 1e-6)),
            [Rule("DN", ValidationRuleOperator.Equals, valueNumber: 50.0)]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void Between_JustBelowMin_Violation()
    {
        var report = _engine.Validate(
            InputWith(NumberParam("DN", 15.0 - 1e-6)),
            [Rule("DN", ValidationRuleOperator.Between, min: 15.0, max: 100.0)]);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void Violation_UnitTypeId_FallsBackToParameterUnit()
    {
        var rule = Rule("DN", ValidationRuleOperator.Between, min: 15.0, max: 100.0);
        var report = _engine.Validate(
            new FamilyValidationInput([new FamilyTypeValidationData("T",
                new ParameterValidationValue[] { new("DN", true, null, 125.0, "unit:mm") })]),
            [rule]);

        var v = Assert.Single(report.Violations);
        Assert.Equal("unit:mm", v.UnitTypeId);
    }

    [Fact]
    public void Violation_UnitTypeId_RuleUnitWins()
    {
        var rule = Rule("DN", ValidationRuleOperator.Between, min: 15.0, max: 100.0);
        rule = rule with { Rule = rule.Rule with { UnitTypeId = "unit:cm" } };
        var report = _engine.Validate(
            new FamilyValidationInput([new FamilyTypeValidationData("T",
                new ParameterValidationValue[] { new("DN", true, null, 125.0, "unit:mm") })]),
            [rule]);

        var v = Assert.Single(report.Violations);
        Assert.Equal("unit:cm", v.UnitTypeId);
    }

    [Fact]
    public void PerType_OneFailingType_FailsFamily()
    {
        var report = _engine.Validate(
            TwoTypeInput(
                [TextParam("Manufacturer", "AGK")],
                [TextParam("Manufacturer", null)]),
            [Rule("Manufacturer", ValidationRuleOperator.HasValue)]);

        Assert.False(report.IsValid);
        var v = Assert.Single(report.Violations);
        Assert.Equal("TypeB", v.TypeName);
    }

    [Fact]
    public void PerType_AllTypesPass_Valid()
    {
        var report = _engine.Validate(
            TwoTypeInput(
                [TextParam("Manufacturer", "AGK")],
                [TextParam("Manufacturer", "Uponor")]),
            [Rule("Manufacturer", ValidationRuleOperator.HasValue)]);

        Assert.True(report.IsValid);
        Assert.Equal(2, report.RulesEvaluated);
    }

    [Fact]
    public void MultipleRules_EachViolationReported()
    {
        var report = _engine.Validate(
            InputWith(AbsentParam("P1"), AbsentParam("P2")),
            [Rule("P1", ValidationRuleOperator.HasValue), Rule("P2", ValidationRuleOperator.IsPresent)]);

        Assert.False(report.IsValid);
        Assert.Equal(2, report.Violations.Count);
        Assert.Equal(2, report.RulesEvaluated);
    }
}
