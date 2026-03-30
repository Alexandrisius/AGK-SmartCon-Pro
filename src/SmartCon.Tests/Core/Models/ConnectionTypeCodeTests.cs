using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class ConnectionTypeCodeTests
{
    [Fact]
    public void Undefined_HasValueZero()
    {
        Assert.Equal(0, ConnectionTypeCode.Undefined.Value);
    }

    [Fact]
    public void Undefined_IsNotDefined()
    {
        Assert.False(ConnectionTypeCode.Undefined.IsDefined);
    }

    [Fact]
    public void DefinedCode_IsDefined()
    {
        var code = new ConnectionTypeCode(1);
        Assert.True(code.IsDefined);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData("999", 999)]
    public void Parse_ValidString_ReturnsCode(string raw, int expected)
    {
        var code = ConnectionTypeCode.Parse(raw);
        Assert.Equal(expected, code.Value);
        Assert.True(code.IsDefined);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    public void Parse_InvalidOrZero_ReturnsUndefined(string? raw)
    {
        var code = ConnectionTypeCode.Parse(raw);
        Assert.Equal(ConnectionTypeCode.Undefined, code);
        Assert.False(code.IsDefined);
    }

    [Fact]
    public void ToString_ReturnsValueString()
    {
        var code = new ConnectionTypeCode(5);
        Assert.Equal("5", code.ToString());
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var a = new ConnectionTypeCode(3);
        var b = new ConnectionTypeCode(3);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentValue_AreNotEqual()
    {
        var a = new ConnectionTypeCode(1);
        var b = new ConnectionTypeCode(2);
        Assert.NotEqual(a, b);
    }
}
