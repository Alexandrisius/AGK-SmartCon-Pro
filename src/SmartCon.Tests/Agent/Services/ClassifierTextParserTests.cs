using System.Text;
using SmartCon.Agent.Services.Tools.Semantic;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class ClassifierTextParserTests
{
    /// <summary>Item as the pinned lists spell it: line|level|code|name|breadcrumb|children.</summary>
    private static string Row(ClassifierItem item) =>
        string.Join("|", item.Line.ToString(), item.Level.ToString(), item.Code, item.Name,
            item.Breadcrumb, string.Join("¶", item.Children));

    private static string LevelCode(int level) =>
        string.Join(".", Enumerable.Repeat("10", level));

    // ----- legacy format -----

    [Fact]
    public void Parse_LegacyThreeLevelText_MatchesThePinnedItemList()
    {
        const string content =
            "# Классификатор проекта\n" +
            "\n" +
            "10 Архитектурные решения\r\n" +
            "\t10.10 Стены и перегородки\n" +
            "\t\t10.10.10 Стены несущие\r\n" +
            "\t\t\tСтены наружные несущие\n" +
            "\t\t\tСтены внутренние несущие\r\n" +
            "\t\t10.10.20 Перегородки\n" +
            "\t\t\tПерегородки кирпичные\r\n" +
            "Заголовок без кода\n" +
            "123 Название не-кода\r\n" +
            "20.10: Задвижки\n" +
            "30.20.10.\tКраны шаровые\r\n" +
            "    30.20 Трубопроводная арматура\n" +
            "        30.20.20 Задвижки\n" +
            "30 Инженерные системы\r\n" +
            "10.20 Проемы\n" +
            "\t10.20.10 Двери\r\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(11, result.Items.Count);
        Assert.Equal(18, result.TotalLines);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.SkippedLines);
        Assert.Equal(new[] { 1, 2, 3 }, result.ParsedLevels);
        // The list is the output of the parser as it was before the wider
        // code forms were accepted: the legacy format must not change.
        Assert.Equal(
            new[]
            {
                "3|1|10|Архитектурные решения|Архитектурные решения|",
                "4|2|10.10|Стены и перегородки|Архитектурные решения › Стены и перегородки|",
                "5|3|10.10.10|Стены несущие|Архитектурные решения › Стены и перегородки › Стены несущие|" +
                "Стены наружные несущие¶Стены внутренние несущие",
                "8|3|10.10.20|Перегородки|Архитектурные решения › Стены и перегородки › Перегородки|" +
                "Перегородки кирпичные",
                "12|2|20.10|Задвижки|Архитектурные решения › Задвижки|",
                "13|3|30.20.10|Краны шаровые|Архитектурные решения › Задвижки › Краны шаровые|",
                "14|2|30.20|Трубопроводная арматура|Архитектурные решения › Трубопроводная арматура|",
                "15|3|30.20.20|Задвижки|Архитектурные решения › Трубопроводная арматура › Задвижки|",
                "16|1|30|Инженерные системы|Инженерные системы|",
                "17|2|10.20|Проемы|Инженерные системы › Проемы|",
                "18|3|10.20.10|Двери|Инженерные системы › Проемы › Двери|"
            },
            result.Items.Select(Row).ToArray());
    }

    [Theory]
    [InlineData("10", 1)]
    [InlineData("00", 1)]
    [InlineData("10.10", 2)]
    [InlineData("10.10.10", 3)]
    [InlineData("01.02.03", 3)]
    [InlineData("99.99.99", 3)]
    public void Parse_LegacyCode_KeepsItsExactShape(string code, int level)
    {
        var result = ClassifierTextParser.Parse(code + "\tНазвание\r\n", maxLines: 10);

        var item = Assert.Single(result.Items);
        Assert.Equal(code, item.Code);
        Assert.Equal("Название", item.Name);
        Assert.Equal(level, item.Level);
    }

    // ----- multi-segment numeric codes -----

    [Theory]
    [InlineData("30.20.10.10", 4)]
    [InlineData("23.10.05.10.11.11", 6)]
    [InlineData("10.10.100", 3)]
    [InlineData("10.10.1000", 3)]
    [InlineData("10.1", 2)]
    [InlineData("1.2.3.4.5", 5)]
    [InlineData("12.345", 2)]
    public void Parse_MultiSegmentNumericCode_IsNotCutToTheLegacyShape(string code, int level)
    {
        var result = ClassifierTextParser.Parse(code + " Название\n", maxLines: 10);

        var item = Assert.Single(result.Items);
        Assert.Equal(code, item.Code);
        Assert.Equal("Название", item.Name);
        Assert.Equal(level, item.Level);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void Parse_FourLevelNumericCodes_StitchTheBreadcrumbThroughEveryLevel()
    {
        const string content =
            "30 Инженерные системы\n" +
            "\t30.20 Трубопроводная арматура\n" +
            "\t\t30.20.10 Краны шаровые\n" +
            "\t\t\t30.20.10.10 Краны шаровые муфтовые\n" +
            "\t\t\t30.20.10.20 Краны шаровые фланцевые\n" +
            "\t\t30.20.20 Задвижки\n" +
            "\t30.30 Насосное оборудование\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(new[] { 1, 2, 3, 4 }, result.ParsedLevels);
        Assert.Equal(
            "Инженерные системы › Трубопроводная арматура › Краны шаровые › Краны шаровые муфтовые",
            result.Items.Single(item => item.Code == "30.20.10.10").Breadcrumb);
        Assert.Equal(
            "Инженерные системы › Трубопроводная арматура › Задвижки",
            result.Items.Single(item => item.Code == "30.20.20").Breadcrumb);
        Assert.Equal(
            "Инженерные системы › Насосное оборудование",
            result.Items.Single(item => item.Code == "30.30").Breadcrumb);
    }

    // ----- letter-prefixed codes -----

    [Theory]
    [InlineData("A30.160", 2)]
    [InlineData("D3010.10", 2)]
    [InlineData("A1010.10", 2)]
    [InlineData("A1010.10.CF", 3)]
    [InlineData("a1010.10", 2)]
    public void Parse_LetterPrefixedCodeWithDots_LevelEqualsSegmentCount(string code, int level)
    {
        var result = ClassifierTextParser.Parse(code + " Название\n", maxLines: 10);

        var item = Assert.Single(result.Items);
        Assert.Equal(code, item.Code);
        Assert.Equal("Название", item.Name);
        Assert.Equal(level, item.Level);
    }

    [Fact]
    public void Parse_LetterPrefixedCodes_BindToTheNearestShorterCode()
    {
        const string content =
            "A1010 Standard Foundations\n" +
            "A1010.10 Wall Foundations\n" +
            "A1010.10.CF Continuous Footings\n" +
            "A1010.20 Column Foundations\n" +
            "A20 Basement Construction\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(new[] { 1, 2, 3 }, result.ParsedLevels);
        Assert.Equal(
            "Standard Foundations › Wall Foundations",
            result.Items.Single(item => item.Code == "A1010.10").Breadcrumb);
        Assert.Equal(
            "Standard Foundations › Wall Foundations › Continuous Footings",
            result.Items.Single(item => item.Code == "A1010.10.CF").Breadcrumb);
        Assert.Equal(
            "Standard Foundations › Column Foundations",
            result.Items.Single(item => item.Code == "A1010.20").Breadcrumb);
    }

    [Fact]
    public void Parse_LetterPrefixedCodeWithoutDots_TakesItsLevelFromIndentation()
    {
        const string content =
            "A10 Foundations\n" +
            "\tA1010 Standard Foundations\n" +
            "\t\tA1010100 Wall Foundations\n" +
            "\tA1020 Special Foundations\n" +
            "A20 Basement Construction\n" +
            "    A2010 Basement Excavation\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(new[] { 1, 2, 3 }, result.ParsedLevels);
        Assert.Equal(
            new[]
            {
                "Foundations",
                "Foundations › Standard Foundations",
                "Foundations › Standard Foundations › Wall Foundations",
                "Foundations › Special Foundations",
                "Basement Construction",
                "Basement Construction › Basement Excavation"
            },
            result.Items.Select(item => item.Breadcrumb).ToArray());
    }

    [Fact]
    public void Parse_LetterPrefixedCodeWithoutDots_AnyDeeperIndentIsOneStep()
    {
        const string content =
            "A10 Foundations\n" +
            "        A1010100 Wall Foundations\n" +
            "A20 Basement Construction\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(
            new[]
            {
                "Foundations",
                "Foundations › Wall Foundations",
                "Basement Construction"
            },
            result.Items.Select(item => item.Breadcrumb).ToArray());
    }

    [Fact]
    public void Parse_LetterPrefixedCodeWithoutDots_FlatFile_IsLevel1()
    {
        const string content =
            "A1010 Standard Foundations\n" +
            "A1020 Special Foundations\n" +
            "A1030 Slab on grade\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(new[] { 1 }, result.ParsedLevels);
        Assert.Equal(
            new[] { "A1010", "A1020", "A1030" },
            result.Items.Select(item => item.Code).ToArray());
        Assert.All(result.Items, item => Assert.Equal(item.Name, item.Breadcrumb));
    }

    [Fact]
    public void Parse_DottedCodeOnDeeperIndent_SegmentCountWins()
    {
        const string content =
            "A10 Foundations\n" +
            "\t\t\tA1010 Standard Foundations\n" +
            "\t\t\t\tA1010.10 Wall Foundations\n" +
            "\t\t\t\t\tA1010100 Column Foundations\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(new[] { 1, 2, 2, 3 }, result.Items.Select(item => item.Level).ToArray());
        Assert.Equal(
            "Foundations › Wall Foundations",
            result.Items.Single(item => item.Code == "A1010.10").Breadcrumb);
        Assert.Equal(
            "Foundations › Wall Foundations › Column Foundations",
            result.Items.Single(item => item.Code == "A1010100").Breadcrumb);
    }

    [Theory]
    [InlineData("A1010x Название")]
    [InlineData("A1010Foundations Название")]
    [InlineData("ABCD12 Название")]
    [InlineData("AB.12 Название")]
    [InlineData("A-1010 Название")]
    [InlineData("А1010 Название")]
    public void Parse_LetterTokenOutsideTheCodeForm_IsNotACode(string line)
    {
        var result = ClassifierTextParser.Parse(line + "\n", maxLines: 10);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.SkippedLines);
    }

    // ----- depth, bare numbers, mixed forms -----

    [Fact]
    public void Parse_TwelveLevelChain_StitchesTheWholeBreadcrumb()
    {
        var content = new StringBuilder();
        var parts = new List<string>();
        for (var level = 1; level <= 12; level++)
        {
            content.Append(LevelCode(level)).Append(" Уровень ").Append(level).Append('\n');
            parts.Add("Уровень " + level);
        }

        var result = ClassifierTextParser.Parse(content.ToString(), maxLines: 2000);

        Assert.Equal(Enumerable.Range(1, 12).ToArray(), result.ParsedLevels);
        Assert.Equal(
            string.Join(ClassifierTextParser.BreadcrumbSeparator, parts),
            result.Items[11].Breadcrumb);
    }

    [Fact]
    public void Parse_ChainLongerThanTheParentStack_GrowsIt()
    {
        var content = new StringBuilder();
        for (var level = 1; level <= 20; level++)
            content.Append(LevelCode(level)).Append(" Уровень ").Append(level).Append('\n');

        var result = ClassifierTextParser.Parse(content.ToString(), maxLines: 2000);

        Assert.Equal(20, result.Items.Count);
        Assert.Equal(20, result.Items[19].Level);
        Assert.Equal(
            20,
            result.Items[19].Breadcrumb.Split(
                new[] { ClassifierTextParser.BreadcrumbSeparator }, StringSplitOptions.None).Length);
    }

    [Theory]
    [InlineData("1000 мм")]
    [InlineData("3 Кухня")]
    [InlineData("123 Название не-кода")]
    [InlineData("2023 год выпуска")]
    [InlineData("1 Раздел")]
    [InlineData("12345.10 Значение")]
    public void Parse_BareNumberAtLineStart_IsNotACode(string line)
    {
        var result = ClassifierTextParser.Parse(line + "\n", maxLines: 10);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void Parse_BareNumberOnDeeperIndent_BecomesChildOfPreviousCode()
    {
        const string content = "10.10.10 Стены несущие\n\t1000 мм\n\t3 Кухня\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        var item = Assert.Single(result.Items);
        Assert.Equal(new[] { "1000 мм", "3 Кухня" }, item.Children);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void Parse_MixedCodeForms_KeepOneHierarchy()
    {
        const string content =
            "10 Архитектура\n" +
            "\tA1010 Foundations\n" +
            "\t\tA1010100 Wall Foundations\n" +
            "20 Инженерные системы\n" +
            "\t20.10 Вентиляция\n" +
            "\t\t20.10.10 Воздуховоды\n" +
            "\t\t\t20.10.10.10 Прямоугольные\n" +
            "1000 мм\n" +
            "3 Кухня\n" +
            "\tA30.160 Кровля\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(2, result.SkippedLines);
        Assert.Equal(new[] { 1, 2, 3, 4 }, result.ParsedLevels);
        Assert.Equal(
            new[]
            {
                "1|1|10|Архитектура|Архитектура|",
                "2|2|A1010|Foundations|Архитектура › Foundations|",
                "3|3|A1010100|Wall Foundations|Архитектура › Foundations › Wall Foundations|",
                "4|1|20|Инженерные системы|Инженерные системы|",
                "5|2|20.10|Вентиляция|Инженерные системы › Вентиляция|",
                "6|3|20.10.10|Воздуховоды|Инженерные системы › Вентиляция › Воздуховоды|",
                "7|4|20.10.10.10|Прямоугольные|" +
                "Инженерные системы › Вентиляция › Воздуховоды › Прямоугольные|",
                "10|2|A30.160|Кровля|Инженерные системы › Кровля|"
            },
            result.Items.Select(Row).ToArray());
    }
}
