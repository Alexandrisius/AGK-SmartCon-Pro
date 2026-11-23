using System.Text.Json.Nodes;
using SmartCon.Agent.Services.Tools;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// read_tool_result pages a stored result as text: non-ASCII values must reach
/// the model as plain letters, not as \uXXXX escapes (six characters per letter
/// that the model misread as different system names).
/// </summary>
public sealed class AgentResultPathTests
{
    [Fact]
    public void ToText_ObjectNode_KeepsCyrillicAsPlainText()
    {
        var node = JsonNode.Parse("{\"name\":\"Таблица-Д2\",\"code\":\"Ц1\"}")!;

        var text = AgentResultPath.ToText(node);

        Assert.Contains("Таблица-Д2", text, StringComparison.Ordinal);
        Assert.Contains("Ц1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToText_StringNode_IsTheStringItself()
    {
        var node = JsonValue.Create("Ведомость объёмов работ");

        var text = AgentResultPath.ToText(node);

        Assert.Equal("Ведомость объёмов работ", text);
    }
}
