using SmartCon.Core.Models;
using SmartCon.Core.Services.Storage;
using Xunit;

namespace SmartCon.Tests.Core.Services.Storage;

public sealed class MappingDefaultsTests
{
    [Fact]
    public void CreatePayload_HasSingleStandardType()
    {
        var payload = MappingDefaults.CreatePayload();

        var type = Assert.Single(payload.ConnectorTypes);
        Assert.Equal(MappingDefaults.StandardTypeCode, type.Code);
        Assert.Equal(MappingDefaults.StandardTypeName, type.Name);
        Assert.Equal(string.Empty, type.Description);
    }

    [Fact]
    public void CreatePayload_HasDirectRuleWithoutFamilies()
    {
        var payload = MappingDefaults.CreatePayload();

        var rule = Assert.Single(payload.MappingRules);
        Assert.Equal(MappingDefaults.StandardTypeCode, rule.FromType.Value);
        Assert.Equal(MappingDefaults.StandardTypeCode, rule.ToType.Value);
        Assert.True(rule.IsDirectConnect);
        Assert.Empty(rule.FittingFamilies);
        Assert.Empty(rule.ReducerFamilies);
    }

    [Fact]
    public void IsUnconfigured_TrueForEmptyMapping()
    {
        Assert.True(MappingDefaults.IsUnconfigured([], []));
    }

    [Fact]
    public void IsUnconfigured_TrueForDefaultMapping()
    {
        Assert.True(MappingDefaults.IsUnconfigured(
            [MappingDefaults.StandardConnectorType],
            [MappingDefaults.StandardDirectRule]));
    }

    [Fact]
    public void IsUnconfigured_False_WhenAdditionalTypeConfigured()
    {
        var types = new[]
        {
            MappingDefaults.StandardConnectorType,
            new ConnectorTypeDefinition { Code = 2, Name = "Резьба" },
        };

        Assert.False(MappingDefaults.IsUnconfigured(types, [MappingDefaults.StandardDirectRule]));
    }

    [Fact]
    public void IsUnconfigured_False_WhenSingleNonStandardTypeConfigured()
    {
        var types = new[] { new ConnectorTypeDefinition { Code = 2, Name = "Резьба" } };

        Assert.False(MappingDefaults.IsUnconfigured(types, []));
    }

    [Fact]
    public void IsUnconfigured_False_WhenRuleHasFittingFamilies()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            FittingFamilies = [new FittingMapping { FamilyName = "Муфта", Priority = 1 }],
        };

        Assert.False(MappingDefaults.IsUnconfigured([MappingDefaults.StandardConnectorType], [rule]));
    }

    [Fact]
    public void IsUnconfigured_False_WhenRuleHasReducerFamilies()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            ReducerFamilies = [new FittingMapping { FamilyName = "Переход", Priority = 1 }],
        };

        Assert.False(MappingDefaults.IsUnconfigured([MappingDefaults.StandardConnectorType], [rule]));
    }
}