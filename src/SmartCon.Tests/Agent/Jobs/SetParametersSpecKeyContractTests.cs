using SmartCon.Core.Models.Agent.Jobs;
using Xunit;

namespace SmartCon.Tests.Agent.Jobs;

/// <summary>
/// Key contract of the set_parameters spec. The rules below mirror
/// SetParametersJobAction.SpecRules; the live rules (and the classification
/// guard behind the flag) are exercised by AgentClassificationToolsTests inside
/// a real Revit.
/// </summary>
public sealed class SetParametersSpecKeyContractTests
{
    private static readonly JobSpecObjectRule[] Rules =
    {
        new()
        {
            Path = string.Empty,
            AllowedKeys = new[]
            {
                "select", "values", "parameter", "guid", "builtInParameter", "scope", "value", "unit",
                "allowUnverifiedClassification"
            }
        },
        new()
        {
            Path = "select",
            AllowedKeys = new[]
            {
                "elementIds", "categoryIds", "categoryNames", "categories", "levelIds", "typeIds",
                "familyName", "familyNames", "typeName", "nameContains", "conditions", "logic",
                "includeTypeParameters", "limit", "systemName", "systemDomain",
                "includeSubComponents"
            }
        },
        new()
        {
            Path = "select.conditions[]",
            AllowedKeys = new[] { "parameter", "op", "value", "unit" }
        },
        new()
        {
            Path = "values[]",
            AllowedKeys = new[] { "select", "value" }
        },
        new()
        {
            Path = "values[].select",
            AllowedKeys = new[]
            {
                "elementIds", "categoryIds", "categoryNames", "categories", "levelIds", "typeIds",
                "familyName", "familyNames", "typeName", "nameContains", "conditions", "logic",
                "includeTypeParameters", "limit", "systemName", "systemDomain",
                "includeSubComponents"
            }
        },
        new()
        {
            Path = "values[].select.conditions[]",
            AllowedKeys = new[] { "parameter", "op", "value", "unit" }
        }
    };

    private const string AssemblyCodeSpec =
        @"{""select"":{""elementIds"":[101]},""builtInParameter"":""UNIFORMAT_CODE"",""scope"":""type"",""value"":""30.10.10""";

    [Fact]
    public void AllowUnverifiedClassification_is_a_known_root_key()
    {
        var error = JobSpecKeyValidator.Validate(
            AssemblyCodeSpec + @",""allowUnverifiedClassification"":true}",
            Rules);

        Assert.Null(error);
    }

    [Fact]
    public void AllowUnverifiedClassification_is_optional()
    {
        Assert.Null(JobSpecKeyValidator.Validate(AssemblyCodeSpec + "}", Rules));
    }

    [Theory]
    [InlineData("allowUnverified")]
    [InlineData("allowUnverifiedCode")]
    [InlineData("skipClassificationCheck")]
    public void Misspelled_flag_is_rejected_and_the_allowed_list_names_the_real_key(string key)
    {
        var error = JobSpecKeyValidator.Validate(
            AssemblyCodeSpec + @",""" + key + @""":true}",
            Rules);

        Assert.NotNull(error);
        Assert.Contains(key, error);
        Assert.Contains("allowUnverifiedClassification", error);
    }

    [Fact]
    public void Flag_is_a_root_key_only()
    {
        var error = JobSpecKeyValidator.Validate(
            @"{""select"":{""elementIds"":[101],""allowUnverifiedClassification"":true}," +
            @"""value"":""30.10.10""}",
            Rules);

        Assert.NotNull(error);
        Assert.Contains("select.allowUnverifiedClassification", error);
    }

    [Theory]
    [InlineData("familyNames")]
    [InlineData("categories")]
    public void Plural_selector_aliases_are_known_keys_of_select(string key)
    {
        // The session fb3b66f6 failed on exactly these keys: the card promised
        // them, the strict validator refused them (S.46, #531).
        var error = JobSpecKeyValidator.Validate(
            @"{""select"":{""" + key + @""":[""Кран шаровой""],""familyName"":""x""}," +
            @"""parameter"":""ADSK_Позиция"",""value"":""1""}",
            Rules);

        Assert.Null(error);
    }

    [Fact]
    public void Values_entries_carry_a_selector_and_a_value_only()
    {
        var ok = JobSpecKeyValidator.Validate(
            @"{""values"":[{""select"":{""categoryIds"":[200]},""value"":""A""}," +
            @"{""select"":{""typeIds"":[300,301]},""value"":""B""}]," +
            @"""parameter"":""ADSK_Позиция""}",
            Rules);
        Assert.Null(ok);

        var unknown = JobSpecKeyValidator.Validate(
            @"{""values"":[{""select"":{""elementIds"":[101]},""value"":""A"",""unit"":""mm""}]," +
            @"""parameter"":""ADSK_Позиция""}",
            Rules);
        Assert.NotNull(unknown);
        Assert.Contains("values[].unit", unknown);
    }

    [Fact]
    public void Values_select_accepts_the_same_keys_as_the_root_select()
    {
        var error = JobSpecKeyValidator.Validate(
            @"{""values"":[{""select"":{""familyNames"":[""Кран""],""categoryNames"":[""Трубы""]}," +
            @"""value"":""A""}],""parameter"":""ADSK_Позиция""}",
            Rules);

        Assert.Null(error);
    }

    [Fact]
    public void Digest_changes_when_the_flag_changes()
    {
        // The flag travels inside the unit arguments (absent when off), so a
        // preview made without it can never authorize an apply made with it.
        var strict = new[]
        {
            new JobUnit
            {
                Seq = 1,
                UnitKey = "uid|UNIFORMAT_CODE|type",
                ArgsJson = @"{""elementId"":101,""value"":""30.10.10"",""scope"":""type""}"
            }
        };
        var optIn = new[]
        {
            new JobUnit
            {
                Seq = 1,
                UnitKey = "uid|UNIFORMAT_CODE|type",
                ArgsJson = @"{""elementId"":101,""value"":""30.10.10"",""scope"":""type""," +
                           @"""allowUnverifiedClassification"":true}"
            }
        };

        Assert.NotEqual(
            JobPreviewDigest.ComputeSelectorHash(strict),
            JobPreviewDigest.ComputeSelectorHash(optIn));
        Assert.Equal(
            JobPreviewDigest.ComputeSelectorHash(strict),
            JobPreviewDigest.ComputeSelectorHash(strict));
        Assert.NotEqual(
            JobPreviewDigest.ComputeSpecHash(AssemblyCodeSpec + "}"),
            JobPreviewDigest.ComputeSpecHash(AssemblyCodeSpec + @",""allowUnverifiedClassification"":true}"));
    }
}
