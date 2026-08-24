using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core;

public sealed class CategoryAutoAssignEngineTests
{
    private readonly CategoryAutoAssignEngine _engine = new(new FamilyValidationEngine());

    private const string CategoryA = "cat-a";
    private const string CategoryB = "cat-b";

    private static ParameterValidationValue TextParam(string name, string? value) =>
        new(name, true, value, null, null);

    private static ParameterValidationValue NumberParam(string name, double? displayNumber) =>
        new(name, true, null, displayNumber, null, displayNumber);

    private static ParameterValidationValue AbsentParam(string name) =>
        new(name, false, null, null, null);

    private static CategoryAutoAssignInput Input(
        params ParameterValidationValue[] values) =>
        new(
            new FamilyValidationInput([new FamilyTypeValidationData("TypeA", values)]),
            new Dictionary<string, string> { ["attr-1"] = "ADSK_Материал", ["attr-2"] = "Комментарий" },
            RevitCategoryOrdinal: -2008049,
            Facts: [new FamilyFact(FamilyFactRuleSet.PartTypeFactKey, "5", "Elbow")],
            FamilyName: "Отвод 90",
            SystemFamilyKey: null);

    private static AssignmentCondition AttrCondition(
        ValidationRuleOperator op,
        string attributeId = "attr-1",
        string? valueText = null,
        double? valueNumber = null,
        double? min = null,
        double? max = null,
        bool isEnabled = true) =>
        new("c1", "g1", AssignmentConditionSourceKind.Attribute, attributeId, null, op,
            valueText, valueNumber, min, max, 0, isEnabled);

    private static AssignmentCondition SysCondition(
        AssignmentSystemField field,
        ValidationRuleOperator op,
        string? valueText = null,
        bool isEnabled = true) =>
        new("c2", "g1", AssignmentConditionSourceKind.System, null, field, op,
            valueText, null, null, null, 1, isEnabled);

    private static AssignmentRuleGroup Group(
        string categoryId,
        params AssignmentCondition[] conditions) =>
        new("g1", categoryId, 0, true, conditions);

    private static AssignmentRuleGroup DisabledGroup(string categoryId, params AssignmentCondition[] conditions) =>
        Group(categoryId, conditions) with { IsEnabled = false };

    [Fact]
    public void Evaluate_NoGroups_NoMatch()
    {
        var result = _engine.Evaluate(Input(), []);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_AllGroupsDisabled_NoMatch()
    {
        var result = _engine.Evaluate(
            Input(TextParam("ADSK_Материал", "сталь")),
            [DisabledGroup(CategoryA, AttrCondition(ValidationRuleOperator.Contains, valueText: "сталь"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_GroupWithoutEnabledConditions_NoMatch()
    {
        var condition = AttrCondition(ValidationRuleOperator.Contains, valueText: "сталь") with { IsEnabled = false };

        var result = _engine.Evaluate(Input(TextParam("ADSK_Материал", "сталь")), [Group(CategoryA, condition)]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_AttributeContains_Matches()
    {
        var result = _engine.Evaluate(
            Input(TextParam("ADSK_Материал", "нержавеющая сталь")),
            [Group(CategoryA, AttrCondition(ValidationRuleOperator.Contains, valueText: "сталь"))]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
        Assert.Equal(CategoryA, result.CategoryId);
    }

    [Fact]
    public void Evaluate_AttributeNumericDisplayUnits_Matches()
    {
        var result = _engine.Evaluate(
            Input(NumberParam("ADSK_Материал", 16)),
            [Group(CategoryA, AttrCondition(ValidationRuleOperator.GreaterThan, valueNumber: 10))]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void Evaluate_AttributeAbsentParameter_DoesNotMatchPositiveOperators()
    {
        var result = _engine.Evaluate(
            Input(AbsentParam("ADSK_Материал")),
            [Group(CategoryA, AttrCondition(ValidationRuleOperator.Contains, valueText: "сталь"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_AttributeAllTypesMustSatisfy_DoesNotMatchOnOneViolation()
    {
        var input = new CategoryAutoAssignInput(
            new FamilyValidationInput(
            [
                new FamilyTypeValidationData("TypeA", [TextParam("ADSK_Материал", "сталь")]),
                new FamilyTypeValidationData("TypeB", [TextParam("ADSK_Материал", "медь")]),
            ]),
            new Dictionary<string, string> { ["attr-1"] = "ADSK_Материал" },
            null, [], "F", null);

        var result = _engine.Evaluate(
            input,
            [Group(CategoryA, AttrCondition(ValidationRuleOperator.Contains, valueText: "сталь"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Theory]
    [InlineData(ValidationRuleOperator.NotEquals)]
    [InlineData(ValidationRuleOperator.NotContains)]
    [InlineData(ValidationRuleOperator.IsEmpty)]
    public void Evaluate_AttributeDisallowedOperator_NeverMatches(ValidationRuleOperator op)
    {
        var result = _engine.Evaluate(
            Input(AbsentParam("ADSK_Материал")),
            [Group(CategoryA, AttrCondition(op, valueText: "сталь"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_DanglingAttributeId_NeverMatches()
    {
        var result = _engine.Evaluate(
            Input(TextParam("ADSK_Материал", "сталь")),
            [Group(CategoryA, AttrCondition(ValidationRuleOperator.Contains, attributeId: "attr-missing", valueText: "сталь"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_SystemRevitCategoryEquals_MatchesByOrdinal()
    {
        var result = _engine.Evaluate(
            Input(),
            [Group(CategoryA, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008049"))]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void Evaluate_SystemRevitCategoryNotEquals_MatchesOtherOrdinal()
    {
        var result = _engine.Evaluate(
            Input(),
            [Group(CategoryA, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.NotEquals, "-2008010"))]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void Evaluate_SystemRevitCategoryUnknown_NeverMatches()
    {
        var input = Input() with { RevitCategoryOrdinal = null };

        var equals = _engine.Evaluate(input,
            [Group(CategoryA, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008049"))]);
        var notEquals = _engine.Evaluate(input,
            [Group(CategoryA, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.NotEquals, "-2008049"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, equals.Outcome);
        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, notEquals.Outcome);
    }

    [Fact]
    public void Evaluate_SystemPartTypeEquals_MatchesByValueKey()
    {
        var result = _engine.Evaluate(
            Input(),
            [Group(CategoryA, SysCondition(AssignmentSystemField.PartType, ValidationRuleOperator.Equals, "5"))]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void Evaluate_SystemPartTypeMissing_NeverMatches()
    {
        var input = Input() with { Facts = [] };

        var result = _engine.Evaluate(
            input,
            [Group(CategoryA, SysCondition(AssignmentSystemField.PartType, ValidationRuleOperator.Equals, "5"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Theory]
    [InlineData(ValidationRuleOperator.Equals, "Отвод 90", true)]
    [InlineData(ValidationRuleOperator.Equals, "отвод 90", true)]
    [InlineData(ValidationRuleOperator.NotEquals, "Тройник", true)]
    [InlineData(ValidationRuleOperator.Contains, "90", true)]
    [InlineData(ValidationRuleOperator.Contains, "тройник", false)]
    public void Evaluate_SystemFamilyName_StringOperators(ValidationRuleOperator op, string value, bool expected)
    {
        var result = _engine.Evaluate(
            Input(),
            [Group(CategoryA, SysCondition(AssignmentSystemField.FamilyName, op, value))]);

        Assert.Equal(expected, result.Outcome == CategoryAutoAssignOutcome.Matched);
    }

    [Fact]
    public void Evaluate_SystemFamilyKey_MatchesInvariantKey()
    {
        var input = Input() with { SystemFamilyKey = "conduit-with-fittings" };

        var result = _engine.Evaluate(
            input,
            [Group(CategoryA, SysCondition(AssignmentSystemField.SystemFamilyKey, ValidationRuleOperator.Equals, "conduit-with-fittings"))]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void Evaluate_SystemFieldDisallowedOperator_NeverMatches()
    {
        var result = _engine.Evaluate(
            Input(),
            [Group(CategoryA, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Contains, "2008"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_OrGroups_SecondGroupMatches()
    {
        var failing = Group(CategoryA, AttrCondition(ValidationRuleOperator.Contains, valueText: "медь"));
        var passing = Group(CategoryA, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008049"));
        passing = passing with { Id = "g2" };

        var result = _engine.Evaluate(Input(), [failing, passing]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void Evaluate_AndInsideGroup_SystemFailsAfterAttributePasses()
    {
        var result = _engine.Evaluate(
            Input(TextParam("ADSK_Материал", "сталь")),
            [Group(CategoryA,
                AttrCondition(ValidationRuleOperator.Contains, valueText: "сталь"),
                SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008010"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Evaluate_DisabledConditionInsideGroup_Ignored()
    {
        var enabled = AttrCondition(ValidationRuleOperator.Contains, valueText: "сталь");
        var disabled = SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008010")
            with { IsEnabled = false };

        var result = _engine.Evaluate(
            Input(TextParam("ADSK_Материал", "сталь")),
            [Group(CategoryA, enabled, disabled)]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
    }

    [Fact]
    public void Evaluate_TwoCategoriesMatch_AmbiguousWithBothIds()
    {
        var result = _engine.Evaluate(
            Input(),
            [
                Group(CategoryB, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008049")),
                Group(CategoryA, SysCondition(AssignmentSystemField.PartType, ValidationRuleOperator.Equals, "5")),
            ]);

        Assert.Equal(CategoryAutoAssignOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.CategoryId);
        Assert.Equal(new[] { CategoryB, CategoryA }, result.CandidateCategoryIds);
    }

    [Fact]
    public void Evaluate_SameCategoryTwoGroups_CountsAsSingleMatch()
    {
        var failing = Group(CategoryA, AttrCondition(ValidationRuleOperator.Contains, valueText: "медь"));
        var passing = Group(CategoryA, SysCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008049"))
            with { Id = "g2" };

        var result = _engine.Evaluate(Input(), [failing, passing]);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
        Assert.Equal(CategoryA, result.CategoryId);
        Assert.Empty(result.CandidateCategoryIds);
    }

    [Fact]
    public void Evaluate_NoMatch_ResultHasNoCandidates()
    {
        var result = _engine.Evaluate(
            Input(TextParam("ADSK_Материал", "сталь")),
            [Group(CategoryA, AttrCondition(ValidationRuleOperator.Contains, valueText: "медь"))]);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
        Assert.Null(result.CategoryId);
        Assert.Empty(result.CandidateCategoryIds);
    }

    [Fact]
    public void Evaluate_AttributeOperatorPolicy_AllowsOnlyPositiveOperators()
    {
        foreach (var op in Enum.GetValues<ValidationRuleOperator>())
        {
            var allowed = AssignmentOperatorPolicy.IsAllowed(AssignmentConditionSourceKind.Attribute, null, op);
            var expected = op is ValidationRuleOperator.IsPresent
                or ValidationRuleOperator.HasValue
                or ValidationRuleOperator.Equals
                or ValidationRuleOperator.Contains
                or ValidationRuleOperator.GreaterThan
                or ValidationRuleOperator.GreaterOrEqual
                or ValidationRuleOperator.LessThan
                or ValidationRuleOperator.LessOrEqual
                or ValidationRuleOperator.Between;

            Assert.Equal(expected, allowed);
        }
    }
}
