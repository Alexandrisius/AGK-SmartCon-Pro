using SmartCon.Core.Models.Agent.Jobs;
using Xunit;

namespace SmartCon.Tests.Agent.Jobs;

/// <summary>
/// Key contract of the replace_family_parameter spec (#430). The unit-test
/// project cannot reference SmartCon.Revit, so the rules below mirror
/// ReplaceFamilyParameterJobAction.SpecRules; the live rules are exercised by the
/// AgentFamilyReplaceJobTests integration suite.
/// </summary>
public sealed class ReplaceFamilySpecKeyContractTests
{
    private static readonly JobSpecObjectRule[] Rules =
    {
        new() { Path = string.Empty, AllowedKeys = new[] { "select", "replacements" } },
        new()
        {
            Path = "select",
            AllowedKeys = new[] { "familyIds", "familyUniqueIds", "familyNames", "nameContains", "categoryIds", "limit" }
        },
        new() { Path = "replacements[]", AllowedKeys = new[] { "source", "target" } },
        new() { Path = "replacements[].source", AllowedKeys = new[] { "name", "guid" } },
        new() { Path = "replacements[].target", AllowedKeys = new[] { "name", "guid" } }
    };

    [Fact]
    public void Replacements_source_and_target_keys_are_known()
    {
        var error = JobSpecKeyValidator.Validate(
            @"{""select"":{""familyNames"":[""A""]},""replacements"":[{""source"":{""name"":""S""," +
            @"""guid"":""0a0b0c0d-1111-2222-3333-444455556666""},""target"":{""guid"":" +
            @"""0a0b0c0d-1111-2222-3333-444455557777""}}]}",
            Rules);

        Assert.Null(error);
    }

    [Fact]
    public void Select_singular_familyName_is_rejected()
    {
        var error = JobSpecKeyValidator.Validate(
            @"{""select"":{""familyName"":""A""},""replacements"":[{""source"":{""name"":""S""}," +
            @"""target"":{""name"":""T""}}]}",
            Rules);

        Assert.NotNull(error);
        Assert.Contains("select.familyName", error!);
        Assert.Contains("familyNames", error);
    }

    [Fact]
    public void Mappings_key_is_rejected_for_replace_action()
    {
        var error = JobSpecKeyValidator.Validate(
            @"{""select"":{},""mappings"":[{""source"":{""name"":""S""},""target"":{""name"":""T""}}]}",
            Rules);

        Assert.NotNull(error);
        Assert.Contains("mappings", error!);
    }

    [Fact]
    public void Scope_key_inside_replacement_refs_is_rejected()
    {
        var error = JobSpecKeyValidator.Validate(
            @"{""select"":{},""replacements"":[{""source"":{""name"":""S"",""scope"":""type""}," +
            @"""target"":{""name"":""T""}}]}",
            Rules);

        Assert.NotNull(error);
        Assert.Contains("replacements[].source.scope", error!);
    }
}
