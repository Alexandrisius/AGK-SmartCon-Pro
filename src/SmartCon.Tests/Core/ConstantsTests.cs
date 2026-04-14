using SmartCon.Core;
using Xunit;

using static SmartCon.Core.Units;
namespace SmartCon.Tests.Core;

public sealed class ConstantsTests
{
    [Fact]
    public void Units_FeetToMm_Is304Point8()
    {
        Assert.Equal(304.8, FeetToMm);
    }

    [Fact]
    public void Units_MmToFeet_IsInverseOfFeetToMm()
    {
        Assert.Equal(1.0 / 304.8, MmToFeet);
    }

    [Fact]
    public void Units_RoundTrip_FeetToMmAndBack()
    {
        const double feet = 1.5;
        double mm = feet * FeetToMm;
        double backToFeet = mm * MmToFeet;
        Assert.Equal(feet, backToFeet, precision: 10);
    }

    [Fact]
    public void Units_KnownConversion_1Foot_Is304Point8Mm()
    {
        Assert.Equal(304.8, 1.0 * FeetToMm);
    }

    [Fact]
    public void Units_KnownConversion_1Mm_Is_MmToFeet()
    {
        double result = 1.0 * MmToFeet;
        Assert.True(result > 0.003280 && result < 0.003282,
            $"1mm in feet should be ~0.003281, got {result}");
    }

    [Fact]
    public void Tolerance_RadiusFt_IsPositive()
    {
        Assert.True(Tolerance.RadiusFt > 0);
        Assert.Equal(1e-5, Tolerance.RadiusFt);
    }

    [Fact]
    public void Tolerance_PositionFt_IsLargerThanRadius()
    {
        Assert.True(Tolerance.PositionFt > Tolerance.RadiusFt);
    }

    [Fact]
    public void Tolerance_AngleDeg_IsOneDegree()
    {
        Assert.Equal(1.0, Tolerance.AngleDeg);
    }

    [Fact]
    public void Tolerance_Default_IsSmall()
    {
        Assert.Equal(1e-9, Tolerance.Default);
    }
}
