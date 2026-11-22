using System.IO;
using System.Text;
using System.Text.Json;
using SmartCon.Agent.Services.Tools.Semantic;
using SmartCon.Core.Models.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class ReadClassifierTests
{
    static ReadClassifierTests()
    {
        // windows-1251 is not a built-in .NET 8 encoding; the tests write and
        // read 1251 files the same way the tool does.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AgentToolContext Context() =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty);

    private static Task<ToolResult> ExecuteAsync(object args) =>
        new ReadClassifierTool().ExecuteAsync(
            Context(), JsonSerializer.SerializeToElement(args), CancellationToken.None);

    private static Task<ToolResult> ExecuteAsync(string argsJson) =>
        new ReadClassifierTool().ExecuteAsync(Context(), ParseJson(argsJson), CancellationToken.None);

    // ----- parser -----

    [Fact]
    public void Parse_MultiLevelCodes_StitchesBreadcrumbs()
    {
        const string content =
            "10 Архитектурные решения\n" +
            "10.10 Стены\n" +
            "10.10.10 Стены наружные\n" +
            "30 Инженерные системы\n" +
            "\t30.20 Трубопроводная арматура\n" +
            "\t\t30.20.10 Краны шаровые\n" +
            "30.30 Насосные установки\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(7, result.Items.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.ParsedLevels);

        var leaf = result.Items.Single(item => item.Code == "30.20.10");
        Assert.Equal(3, leaf.Level);
        Assert.Equal(
            "Инженерные системы › Трубопроводная арматура › Краны шаровые",
            leaf.Breadcrumb);
        Assert.Equal(6, leaf.Line);

        // Indentation does not drive coded lines: a flat level-2 code still
        // binds to the level-1 parent, not to the indented level-2 above it.
        var pumps = result.Items.Single(item => item.Code == "30.30");
        Assert.Equal("Инженерные системы › Насосные установки", pumps.Breadcrumb);
    }

    [Fact]
    public void Parse_TrailingDotAndTabSeparators_AreTolerated()
    {
        const string content = "30.20.10.\tКраны шаровые\r\n20.10: Задвижки\r\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Краны шаровые", result.Items[0].Name);
        Assert.Equal("Задвижки", result.Items[1].Name);
    }

    [Fact]
    public void Parse_UncodedIndentedLines_BecomeChildrenOfPreviousCode()
    {
        const string content =
            "10.20 Проемы\n" +
            "\t10.20.10 Двери\n" +
            "\t\tДвери однопольные\n" +
            "\t\tДвери двупольные\n" +
            "\t10.20.20 Окна\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        var door = result.Items.Single(item => item.Code == "10.20.10");
        Assert.Equal(new[] { "Двери однопольные", "Двери двупольные" }, door.Children);
        var window = result.Items.Single(item => item.Code == "10.20.20");
        Assert.Empty(window.Children);
    }

    [Fact]
    public void Parse_CommentsEmptyLinesAndHeaders_KeepPhysicalLineNumbers()
    {
        const string content =
            "# Классификатор проекта\n" +
            "// внутренний комментарий\n" +
            "\n" +
            "Заголовок без кода\n" +
            "10 Раздел\n" +
            "10.10 Подраздел\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(5, result.Items[0].Line);
        Assert.Equal(6, result.Items[1].Line);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void Parse_ThreeDigitNumbers_AreNotCodes()
    {
        const string content = "123 Название не-кода\n2023 год выпуска\n";

        var result = ClassifierTextParser.Parse(content, maxLines: 2000);

        Assert.Empty(result.Items);
        Assert.Equal(2, result.SkippedLines);
    }

    [Fact]
    public void Parse_MoreLinesThanMaxLines_Truncates()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 10; index++)
            builder.Append(index).Append("0.10 Элемент ").Append(index).Append('\n');

        var result = ClassifierTextParser.Parse(builder.ToString(), maxLines: 5);

        Assert.True(result.Truncated);
        Assert.Equal(10, result.TotalLines);
        Assert.Equal(5, result.Items.Count);
    }

    // ----- tool -----

    [Fact]
    public async Task Execute_ParsesUtf8File_IntoReadyItems()
    {
        var content =
            "10 Архитектурные решения\n" +
            "10.20 Проемы\n" +
            "10.20.10 Двери\n";

        using var file = TempFile.WithContent(content, Encoding.UTF8);

        var result = await ExecuteAsync(new { path = file.Path });

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.Equal(3, data.GetProperty("total").GetInt32());
        Assert.Equal(
            new[] { 1, 2, 3 },
            data.GetProperty("parsedLevels").EnumerateArray().Select(e => e.GetInt32()));

        var door = data.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "10.20.10");
        Assert.Equal(
            "Архитектурные решения › Проемы › Двери",
            door.GetProperty("breadcrumb").GetString());
        Assert.Equal(file.Path, data.GetProperty("path").GetString());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.Contains("semantic_match", data.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task Execute_Windows1251File_AutoFallsBack()
    {
        const string content = "30.20.10 Краны шаровые латунные муфтовые\n";

        using var file = TempFile.WithContent(
            content, Encoding.GetEncoding("windows-1251"));

        var result = await ExecuteAsync(new { path = file.Path });

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!.Value.GetProperty("items").EnumerateArray());
        Assert.Equal("Краны шаровые латунные муфтовые", item.GetProperty("name").GetString());
        Assert.Equal(
            "Краны шаровые латунные муфтовые",
            item.GetProperty("breadcrumb").GetString());
    }

    [Fact]
    public async Task Execute_ExplicitEncoding_IsHonoured()
    {
        const string content = "10 Раздел классификатора\n";

        using var file = TempFile.WithContent(
            content, Encoding.GetEncoding("windows-1251"));

        var result = await ExecuteAsync(new { path = file.Path, encoding = "windows-1251" });

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!.Value.GetProperty("items").EnumerateArray());
        Assert.Equal("Раздел классификатора", item.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Execute_TruncatedLongFile_Warns()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 30; index++)
            builder.Append("10.").Append(index.ToString("00")).Append(" Элемент\n");

        using var file = TempFile.WithContent(builder.ToString(), Encoding.UTF8);

        var result = await ExecuteAsync(new { path = file.Path, maxLines = 10 });

        Assert.True(result.Success);
        Assert.True(result.Data!.Value.GetProperty("truncated").GetBoolean());
        Assert.Equal(10, result.Data!.Value.GetProperty("total").GetInt32());
        Assert.Contains(result.Warnings, warning => warning.Contains("maxLines"));
    }

    [Fact]
    public async Task Execute_MissingFile_ReturnsTypedError()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), "smartcon-missing-" + Guid.NewGuid() + ".txt");

        var result = await ExecuteAsync(new { path = missing });

        Assert.False(result.Success);
        Assert.Equal("file_not_found", result.ErrorCode);
    }

    [Theory]
    [InlineData("{\"path\":\"relative.txt\"}")]
    [InlineData("{\"path\":\"\"}")]
    [InlineData("{}")]
    [InlineData("{\"path\":\"C:\\\\ok.txt\",\"maxLines\":\"many\"}")]
    public async Task Execute_InvalidArguments_AreRejected(string argsJson)
    {
        var result = await ExecuteAsync(argsJson);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task Execute_NoCodedLines_WarnsAndReturnsEmptyItems()
    {
        using var file = TempFile.WithContent("просто текст без кодов\n", Encoding.UTF8);

        var result = await ExecuteAsync(new { path = file.Path });

        Assert.True(result.Success);
        Assert.Equal(0, result.Data!.Value.GetProperty("total").GetInt32());
        Assert.Contains(result.Warnings, warning => warning.Contains("coded lines"));
    }

    private sealed class TempFile : IDisposable
    {
        private TempFile(string path) => Path = path;

        public string Path { get; }

        public static TempFile WithContent(string content, Encoding encoding)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "smartcon-classifier-" + Guid.NewGuid() + ".txt");
            File.WriteAllText(path, content, encoding);
            return new TempFile(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
