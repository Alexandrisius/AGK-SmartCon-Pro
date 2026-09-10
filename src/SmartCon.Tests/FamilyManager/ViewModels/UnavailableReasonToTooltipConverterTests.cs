using System.Globalization;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.UI.Converters;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Tests for <see cref="UnavailableReasonToTooltipConverter"/>. The converter
/// reads localized strings through <see cref="LocalizationService.CurrentLanguage"/>,
/// a process-wide global — so this class is serialized with the language
/// mutators via the shared "Localization" collection and pins the language
/// explicitly (see #170; convention documented in PartTypeLabelMapTests).
/// </summary>
[Collection("Localization")]
public sealed class UnavailableReasonToTooltipConverterTests : IDisposable
{
    private readonly UnavailableReasonToTooltipConverter _converter = new();
    private readonly Language _originalLanguage;

    public UnavailableReasonToTooltipConverterTests()
    {
        _originalLanguage = LocalizationService.CurrentLanguage;
        LocalizationService.SetLanguage(Language.RU);
    }

    public void Dispose() => LocalizationService.SetLanguage(_originalLanguage);

    private object? Convert(FamilyUnavailableReason reason, object? required, object? current, bool hasCompatible)
        => _converter.Convert(
            new object[] { reason, required ?? DBNull.Value, current ?? DBNull.Value, hasCompatible },
            typeof(string), new object(), CultureInfo.InvariantCulture);

    [Fact]
    public void None_ReturnsNull()
    {
        Assert.Null(Convert(FamilyUnavailableReason.None, null, 2025, true));
    }

    [Fact]
    public void Deprecated_ReturnsDeprecatedTextWithRemedy()
    {
        var result = Convert(FamilyUnavailableReason.Deprecated, null, 2025, true) as string;

        Assert.NotNull(result);
        Assert.Contains("загрузка в проект запрещена", result);
        Assert.Contains("Актуальное", result);
    }

    [Fact]
    public void RevitVersion_WithCompatible_SuggestsMakeActive()
    {
        var result = Convert(FamilyUnavailableReason.RevitVersion, 2025, 2024, hasCompatible: true) as string;

        Assert.NotNull(result);
        Assert.Contains("2025", result);
        Assert.Contains("2024", result);
        Assert.Contains("Версии", result);
    }

    [Fact]
    public void RevitVersion_WithoutCompatible_StatesNoCompatibleVersion()
    {
        var result = Convert(FamilyUnavailableReason.RevitVersion, 2025, 2024, hasCompatible: false) as string;

        Assert.NotNull(result);
        Assert.Contains("2025", result);
        Assert.Contains("2024", result);
        Assert.Contains("нет версии", result);
    }

    [Fact]
    public void Both_CombinesDeprecatedAndRevitText()
    {
        var result = Convert(FamilyUnavailableReason.DeprecatedAndRevitVersion, 2025, 2024, hasCompatible: false) as string;

        Assert.NotNull(result);
        Assert.Contains("загрузка в проект запрещена", result);
        Assert.Contains("2025", result);
    }
}
