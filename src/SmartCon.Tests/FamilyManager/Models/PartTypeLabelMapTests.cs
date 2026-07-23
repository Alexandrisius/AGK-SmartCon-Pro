using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// Tests for <see cref="PartTypeLabelMap"/> (ADR-055): localized labels for
/// stored Part Type ordinals with fallback semantics for unknown keys.
/// </summary>
public sealed class PartTypeLabelMapTests
{
    [Theory]
    [InlineData(5, "Отвод")]
    [InlineData(6, "Тройник")]
    [InlineData(7, "Переход")]
    [InlineData(8, "Крестовина")]
    [InlineData(13, "Соединение")]
    [InlineData(28, "Мультипорт")]
    [InlineData(32, "Фланец")]
    [InlineData(60, "Механическое сочленение")]
    [InlineData(-1, "Не определён")]
    public void TryGetLabel_Russian_ReturnsExpected(int ordinal, string expected)
    {
        var previous = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.RU);
        try
        {
            Assert.Equal(expected, PartTypeLabelMap.TryGetLabel(ordinal));
            Assert.Equal(expected, PartTypeLabelMap.TryGetLabel(ordinal.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            LocalizationService.SetLanguage(previous);
        }
    }

    [Theory]
    [InlineData(5, "Elbow")]
    [InlineData(6, "Tee")]
    [InlineData(-1, "Undefined")]
    public void TryGetLabel_English_ReturnsExpected(int ordinal, string expected)
    {
        var previous = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.EN);
        try
        {
            Assert.Equal(expected, PartTypeLabelMap.TryGetLabel(ordinal));
        }
        finally
        {
            LocalizationService.SetLanguage(previous);
        }
    }

    [Fact]
    public void TryGetLabel_UnknownOrdinal_ReturnsNull()
    {
        Assert.Null(PartTypeLabelMap.TryGetLabel(61));
        Assert.Null(PartTypeLabelMap.TryGetLabel("61"));
    }

    [Fact]
    public void TryGetLabel_NonNumericKey_ReturnsNull()
    {
        Assert.Null(PartTypeLabelMap.TryGetLabel("abc"));
    }

    [Fact]
    public void TryGetLabel_EmptySentinel_ReturnsNull()
    {
        Assert.Null(PartTypeLabelMap.TryGetLabel(string.Empty));
    }
}
