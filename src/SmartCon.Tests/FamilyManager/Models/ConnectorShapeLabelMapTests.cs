using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// Tests for <see cref="ConnectorShapeLabelMap"/> (owner stress test
/// 2026-09-01): localized labels for the connector_shape bitmask — a
/// multi-shape transition reads «Круглый и Прямоугольный», never the
/// extraction-time «Round+Rectangular» fallback.
/// Serialized with other <see cref="LocalizationService.CurrentLanguage"/>
/// mutators — the language is a process-wide global.
/// </summary>
[Collection("Localization")]
public sealed class ConnectorShapeLabelMapTests
{
    [Theory]
    [InlineData(1, "Круглый")]
    [InlineData(2, "Прямоугольный")]
    [InlineData(4, "Овальный")]
    [InlineData(3, "Круглый и Прямоугольный")]
    [InlineData(5, "Круглый и Овальный")]
    [InlineData(6, "Прямоугольный и Овальный")]
    [InlineData(7, "Круглый, Прямоугольный и Овальный")]
    public void TryGetLabel_Russian_ReturnsExpected(int mask, string expected)
    {
        var previous = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.RU);
        try
        {
            Assert.Equal(expected, ConnectorShapeLabelMap.TryGetLabel(mask.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            LocalizationService.SetLanguage(previous);
        }
    }

    [Theory]
    [InlineData(1, "Round")]
    [InlineData(3, "Round and Rectangular")]
    [InlineData(7, "Round, Rectangular and Oval")]
    public void TryGetLabel_English_ReturnsExpected(int mask, string expected)
    {
        var previous = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.EN);
        try
        {
            Assert.Equal(expected, ConnectorShapeLabelMap.TryGetLabel(mask.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            LocalizationService.SetLanguage(previous);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("8")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    public void TryGetLabel_UnknownMask_ReturnsNull(string? valueKey)
    {
        Assert.Null(ConnectorShapeLabelMap.TryGetLabel(valueKey));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData(null, false)]
    public void IsZeroMask_DetectsEvaluatedZeroOnly(string? valueKey, bool expected)
    {
        Assert.Equal(expected, ConnectorShapeLabelMap.IsZeroMask(valueKey));
    }

    [Fact]
    public void NoConnectorsLabel_FollowsLanguage()
    {
        var previous = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.SetLanguage(Language.RU);
            Assert.Equal("Нет коннекторов", ConnectorShapeLabelMap.NoConnectorsLabel);
            LocalizationService.SetLanguage(Language.EN);
            Assert.Equal("No connectors", ConnectorShapeLabelMap.NoConnectorsLabel);
        }
        finally
        {
            LocalizationService.SetLanguage(previous);
        }
    }
}
