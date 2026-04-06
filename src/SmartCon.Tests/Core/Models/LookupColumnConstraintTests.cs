using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

/// <summary>
/// Тесты LookupColumnConstraint — модель ограничения multi-column lookup.
/// </summary>
public sealed class LookupColumnConstraintTests
{
    [Fact]
    public void Record_StoresAllProperties()
    {
        var c = new LookupColumnConstraint(ConnectorIndex: 2, ParameterName: "BP_NominalDiameter_2", ValueMm: 15.0);

        Assert.Equal(2, c.ConnectorIndex);
        Assert.Equal("BP_NominalDiameter_2", c.ParameterName);
        Assert.Equal(15.0, c.ValueMm);
    }

    [Fact]
    public void Record_Equality_SameValues_AreEqual()
    {
        var a = new LookupColumnConstraint(1, "DN", 20.0);
        var b = new LookupColumnConstraint(1, "DN", 20.0);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Record_Equality_DifferentValues_AreNotEqual()
    {
        var a = new LookupColumnConstraint(1, "DN", 20.0);
        var b = new LookupColumnConstraint(1, "DN", 25.0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Record_Equality_DifferentParamName_AreNotEqual()
    {
        var a = new LookupColumnConstraint(1, "DN1", 20.0);
        var b = new LookupColumnConstraint(1, "DN2", 20.0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Record_With_CreatesNewInstance()
    {
        var original = new LookupColumnConstraint(1, "DN", 15.0);
        var modified = original with { ValueMm = 25.0 };

        Assert.Equal(15.0, original.ValueMm);
        Assert.Equal(25.0, modified.ValueMm);
        Assert.Equal("DN", modified.ParameterName);
    }

    [Fact]
    public void ToString_ContainsAllFields()
    {
        var c = new LookupColumnConstraint(3, "BP_NominalDiameter", 32.0);
        var str = c.ToString();

        Assert.Contains("3", str);
        Assert.Contains("BP_NominalDiameter", str);
        Assert.Contains("32", str);
    }

    [Fact]
    public void ZeroValueMm_IsValid()
    {
        var c = new LookupColumnConstraint(0, "Param", 0.0);
        Assert.Equal(0.0, c.ValueMm);
    }
}
