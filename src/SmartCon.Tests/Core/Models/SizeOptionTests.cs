using SmartCon.Core.Models;
using Xunit;
using System;

namespace SmartCon.Tests.Core.Models;

/// <summary>
/// Тесты SizeOption: IsAutoSelect, DisplayName, ToString.
/// </summary>
public sealed class SizeOptionTests
{
    [Fact]
    public void AutoSelectOption_HasEmptySourceAndFlag()
    {
        var option = new SizeOption
        {
            DisplayName = "АВТОПОДБОР (DN 32)",
            Radius = 0.052493,
            Source = "",
            IsAutoSelect = true,
        };

        Assert.True(option.IsAutoSelect);
        Assert.Empty(option.Source);
        Assert.Equal("АВТОПОДБОР (DN 32)", option.DisplayName);
    }

    [Fact]
    public void RegularOption_HasSourceAndNotAutoSelect()
    {
        var option = new SizeOption
        {
            DisplayName = "DN 25",
            Radius = 0.041339,
            Source = "LookupTable",
            IsAutoSelect = false,
        };

        Assert.False(option.IsAutoSelect);
        Assert.Equal("LookupTable", option.Source);
    }

    [Fact]
    public void ToString_ReturnsDisplayName()
    {
        var option = new SizeOption
        {
            DisplayName = "DN 50",
            Radius = 0.082677,
            Source = "FamilySymbol",
            IsAutoSelect = false,
        };

        Assert.Equal("DN 50", option.ToString());
    }

    [Fact]
    public void PipeTypeSource_IsNotAutoSelect()
    {
        var option = new SizeOption
        {
            DisplayName = "DN 32",
            Radius = 0.052493,
            Source = "PipeType",
            IsAutoSelect = false,
        };

        Assert.False(option.IsAutoSelect);
        Assert.Equal("PipeType", option.Source);
    }

    [Fact]
    public void RadiusConversion_DN32_ToFeet()
    {
        double dn32Mm = 32.0;
        double radiusInFeet = (dn32Mm / 2.0) / 304.8;

        var option = new SizeOption
        {
            DisplayName = "DN 32",
            Radius = radiusInFeet,
            Source = "LookupTable",
            IsAutoSelect = false,
        };

        var recoveredDn = (int)System.Math.Round(option.Radius * 2.0 * 304.8);
        Assert.Equal(32, recoveredDn);
    }

    [Fact]
    public void RadiusConversion_DN25_ToFeet()
    {
        double dn25Mm = 25.0;
        double radiusInFeet = (dn25Mm / 2.0) / 304.8;

        var option = new SizeOption
        {
            DisplayName = "DN 25",
            Radius = radiusInFeet,
            Source = "FamilySymbol",
            IsAutoSelect = false,
        };

        var recoveredDn = (int)System.Math.Round(option.Radius * 2.0 * 304.8);
        Assert.Equal(25, recoveredDn);
    }
}
