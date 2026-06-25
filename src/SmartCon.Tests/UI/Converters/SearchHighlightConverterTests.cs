using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SmartCon.Tests.UI;
using SmartCon.UI.Converters;
using Xunit;

namespace SmartCon.Tests.UI.Converters;

public sealed class SearchHighlightConverterTests
{
    private static void RunSta(Action testBody)
    {
        StaThreadRunner.Run(testBody);
    }

    private static object Convert(string? text, string? search)
    {
        var converter = new SearchHighlightConverter();
        var values = new object[] { (object?)text ?? string.Empty, (object?)search ?? string.Empty };
        return converter.Convert(
            values,
            typeof(TextBlock),
            parameter: null!,
            CultureInfo.InvariantCulture);
    }

    private static TextBlock AsTextBlock(object result)
    {
        if (result is TextBlock tb)
        {
            return tb;
        }

        Assert.IsType<string>(result);
        return new TextBlock { Inlines = { new Run((string)result) } };
    }

    private static string AsString(object result)
    {
        if (result is string s)
        {
            return s;
        }

        Assert.Fail($"Expected string but got {result?.GetType().Name ?? "null"}");
        return string.Empty;
    }

    private static string Flatten(TextBlock tb)
    {
        return string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text ?? string.Empty));
    }

    private static int CountHighlightedRuns(TextBlock tb)
    {
        int count = 0;
        foreach (var inline in tb.Inlines)
        {
            if (inline is Run run && run.Background is not null)
            {
                count++;
            }
        }
        return count;
    }

    [Fact]
    public void Convert_NullText_ReturnsEmptyString()
    {
        RunSta(() =>
        {
            var result = Convert(null, "filter");
            Assert.Equal(string.Empty, Flatten(AsTextBlock(result)));
        });
    }

    [Fact]
    public void Convert_EmptyText_ReturnsEmptyString()
    {
        RunSta(() =>
        {
            var result = Convert(string.Empty, "filter");
            Assert.Equal(string.Empty, Flatten(AsTextBlock(result)));
        });
    }

    [Fact]
    public void Convert_NullSearch_ReturnsTextAsIs()
    {
        RunSta(() =>
        {
            var result = Convert("Фильтр-50 Ду", null);
            Assert.IsType<string>(result);
            Assert.Equal("Фильтр-50 Ду", AsString(result));
        });
    }

    [Fact]
    public void Convert_WhitespaceSearch_ReturnsTextAsIs()
    {
        RunSta(() =>
        {
            var result = Convert("Фильтр-50 Ду", "   ");
            Assert.IsType<string>(result);
            Assert.Equal("Фильтр-50 Ду", AsString(result));
        });
    }

    [Fact]
    public void Convert_NoMatch_ReturnsPlainTextInSingleRun()
    {
        RunSta(() =>
        {
            var result = Convert("Фильтр-50 Ду", "xyz");
            var tb = AsTextBlock(result);
            Assert.Equal("Фильтр-50 Ду", Flatten(tb));
            Assert.Equal(0, CountHighlightedRuns(tb));
        });
    }

    [Fact]
    public void Convert_SingleMatch_HighlightsMatchingSubstring()
    {
        RunSta(() =>
        {
            var result = Convert("Фильтр-50 Ду", "фильтр");
            var inlines = AsTextBlock(result).Inlines.OfType<Run>().ToList();

            Assert.Equal(2, inlines.Count);
            Assert.Equal("Фильтр", inlines[0].Text);
            Assert.Equal("-50 Ду", inlines[1].Text);
            Assert.NotNull(inlines[0].Background);
            Assert.Equal(FontWeights.SemiBold, inlines[0].FontWeight);
        });
    }

    [Fact]
    public void Convert_MatchInTheMiddle_SplitsTextIntoThreeRuns()
    {
        RunSta(() =>
        {
            var result = Convert("abc-FILTER-xyz", "filter");
            var inlines = AsTextBlock(result).Inlines.OfType<Run>().ToList();

            Assert.Equal(3, inlines.Count);
            Assert.Equal("abc-", inlines[0].Text);
            Assert.Equal("FILTER", inlines[1].Text);
            Assert.Equal("-xyz", inlines[2].Text);
            Assert.NotNull(inlines[1].Background);
        });
    }

    [Fact]
    public void Convert_MultipleMatchesOfSameToken_HighlightsAll()
    {
        RunSta(() =>
        {
            var result = Convert("filter-1 filter-2 filter-3", "filter");
            var tb = AsTextBlock(result);
            Assert.Equal(3, CountHighlightedRuns(tb));
            Assert.Equal("filter-1 filter-2 filter-3", Flatten(tb));
        });
    }

    [Fact]
    public void Convert_OverlappingMatches_MergedIntoSingleRun()
    {
        RunSta(() =>
        {
            var result = Convert("abcabc", "abcab");
            var tb = AsTextBlock(result);
            Assert.Equal(1, CountHighlightedRuns(tb));
            Assert.Equal("abcabc", Flatten(tb));
        });
    }

    [Fact]
    public void Convert_CaseInsensitive_MatchesRegardlessOfCase()
    {
        RunSta(() =>
        {
            var result = Convert("Filter-FILTER-FiLtEr", "filter");
            Assert.Equal(3, CountHighlightedRuns(AsTextBlock(result)));
        });
    }

    [Fact]
    public void Convert_MultiTokenSearch_HighlightsEachToken()
    {
        RunSta(() =>
        {
            var result = Convert("Фильтр-50 Ду обратный", "фильтр ду");
            var inlines = AsTextBlock(result).Inlines.OfType<Run>().ToList();

            Assert.Equal(2, CountHighlightedRuns(AsTextBlock(result)));
            Assert.Contains(inlines, r => r.Text == "Фильтр");
            Assert.Contains(inlines, r => r.Text == "Ду");
        });
    }

    [Fact]
    public void Convert_MultiTokenSearch_OnlyOneTokenMatches_StillHighlightsOther()
    {
        RunSta(() =>
        {
            var result = Convert("Фильтр-50", "фильтр xyz");
            var tb = AsTextBlock(result);
            Assert.Equal(1, CountHighlightedRuns(tb));
            Assert.Equal("Фильтр-50", Flatten(tb));
        });
    }

    [Fact]
    public void Convert_MatchAtStart_ProducesTwoRuns()
    {
        RunSta(() =>
        {
            var result = Convert("Filter-valve", "filter");
            var inlines = AsTextBlock(result).Inlines.OfType<Run>().ToList();
            Assert.Equal(2, inlines.Count);
            Assert.Equal("Filter", inlines[0].Text);
            Assert.Equal("-valve", inlines[1].Text);
        });
    }

    [Fact]
    public void Convert_MatchAtEnd_ProducesTwoRuns()
    {
        RunSta(() =>
        {
            var result = Convert("Pipe-Filter", "filter");
            var inlines = AsTextBlock(result).Inlines.OfType<Run>().ToList();
            Assert.Equal(2, inlines.Count);
            Assert.Equal("Pipe-", inlines[0].Text);
            Assert.Equal("Filter", inlines[1].Text);
        });
    }

    [Fact]
    public void Convert_EmptySearchAfterTrim_NoHighlights()
    {
        RunSta(() =>
        {
            var result = Convert("Filter", "   ");
            Assert.IsType<string>(result);
            Assert.Equal("Filter", AsString(result));
        });
    }

    [Fact]
    public void Convert_SearchShorterThanText_DoesNotCrashOnPartialMatch()
    {
        RunSta(() =>
        {
            var result = Convert("Filter", "f");
            var inlines = AsTextBlock(result).Inlines.OfType<Run>().ToList();
            Assert.Equal(2, inlines.Count);
            Assert.Equal("F", inlines[0].Text);
            Assert.Equal("ilter", inlines[1].Text);
        });
    }

    [Fact]
    public void Convert_ConsecutiveMatchesWithoutSeparator_MergeIntoOneRun()
    {
        RunSta(() =>
        {
            var result = Convert("ababab", "ab");
            var inlines = AsTextBlock(result).Inlines.OfType<Run>().ToList();
            Assert.Single(inlines);
            Assert.Equal("ababab", inlines[0].Text);
            Assert.NotNull(inlines[0].Background);
        });
    }

    [Fact]
    public void Convert_PreservesOriginalCasingInOutput()
    {
        RunSta(() =>
        {
            var result = Convert("Filter-FILTER", "filter");
            Assert.Equal("Filter-FILTER", Flatten(AsTextBlock(result)));
        });
    }

    [Fact]
    public void Convert_NullValuesArray_ReturnsEmptyString()
    {
        RunSta(() =>
        {
            var converter = new SearchHighlightConverter();
            var result = converter.Convert((object[]?)null!, typeof(TextBlock), parameter: null!, CultureInfo.InvariantCulture);
            Assert.Equal(string.Empty, AsString(result));
        });
    }

    [Fact]
    public void Convert_SingleValueArray_ReturnsFirstValue()
    {
        RunSta(() =>
        {
            var converter = new SearchHighlightConverter();
            var result = converter.Convert(
                new object[] { (object)"hello" },
                typeof(TextBlock),
                parameter: null!,
                CultureInfo.InvariantCulture);
            Assert.Equal("hello", AsString(result));
        });
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            new SearchHighlightConverter().ConvertBack(null!, new[] { typeof(string) }, parameter: null!, CultureInfo.InvariantCulture));
    }
}
