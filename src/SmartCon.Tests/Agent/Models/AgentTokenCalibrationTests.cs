using Xunit;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Models;

public sealed class AgentTokenCalibrationTests
{
    [Fact]
    public void Observe_FirstSample_SnapsFactorToRatio()
    {
        var calibration = new AgentTokenCalibration();

        calibration.Observe(100, 120);

        Assert.Equal(1.2, calibration.Factor, 10);
        Assert.Equal(1, calibration.Samples);
    }

    [Fact]
    public void Observe_SecondSample_AppliesEma()
    {
        var calibration = new AgentTokenCalibration();

        calibration.Observe(100, 120);
        calibration.Observe(100, 80);

        Assert.Equal(1.08, calibration.Factor, 10);
        Assert.Equal(2, calibration.Samples);
    }

    [Fact]
    public void Observe_OutlierRatios_AreClampedToRange()
    {
        var high = new AgentTokenCalibration();
        high.Observe(10, 1000);

        var low = new AgentTokenCalibration();
        low.Observe(1000, 10);

        Assert.Equal(2.0, high.Factor, 10);
        Assert.Equal(0.5, low.Factor, 10);
    }

    [Fact]
    public void Observe_ZeroOrNegativeValues_AreIgnored()
    {
        var calibration = new AgentTokenCalibration();

        calibration.Observe(0, 100);
        calibration.Observe(100, 0);
        calibration.Observe(-10, 100);

        Assert.Equal(1.0, calibration.Factor, 10);
        Assert.Equal(0, calibration.Samples);
    }

    [Fact]
    public void Apply_ScalesRawEstimateAndNeverGoesNegative()
    {
        var calibration = new AgentTokenCalibration();

        Assert.Equal(100, calibration.Apply(100));

        calibration.Observe(100, 150);

        Assert.Equal(150, calibration.Apply(100));
        Assert.Equal(0, calibration.Apply(0));
        Assert.Equal(0, calibration.Apply(-10));
    }
}
