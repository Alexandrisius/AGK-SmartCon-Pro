using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core;

public sealed class DisplayValueParserTests
{
    [Theory]
    [InlineData("300 мм", 300.0)]
    [InlineData("16 бар", 16.0)]
    [InlineData("0.164042", 0.164042)]
    [InlineData("-5.5 м", -5.5)]
    [InlineData("42", 42.0)]
    [InlineData("1 200 мм", 1200.0)]
    [InlineData("1 200 мм", 1200.0)]
    [InlineData("1,200 mm", 1200.0)]
    [InlineData("1,234,567", 1234567.0)]
    [InlineData("1.234.567", 1234567.0)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("16,5", 16.5)]
    [InlineData("1,5", 1.5)]
    [InlineData("16,500 бар", 16.5)]
    [InlineData("abc", null)]
    [InlineData("мм", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("1'-6\"", null)]
    [InlineData("1/2\"", null)]
    public void TryParseNumber_Cases(string? input, double? expected)
    {
        Assert.Equal(expected, DisplayValueParser.TryParseNumber(input));
    }
}
