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

    [Fact]
    public void Resolve_PropertySegmentOverArray_MapsToColumnOfScalars()
    {
        // data.rows.typeId: a property name applied to an array of rows yields
        // the column - the shape a $result argument needs for select.typeIds
        // (S.47, #532). A row without the property keeps its place as null.
        const string json =
            "{\"data\":{\"rows\":[" +
            "{\"elementId\":11,\"typeId\":101}," +
            "{\"elementId\":12,\"typeId\":102}," +
            "{\"elementId\":13}]}," +
            "\"message\":\"ok\"}";

        var resolved = AgentResultPath.Resolve(json, "data.rows.typeId", out var error);

        Assert.Null(error);
        var column = Assert.IsType<JsonArray>(resolved);
        Assert.Equal(3, column.Count);
        Assert.Equal(101, (int)column[0]!);
        Assert.Equal(102, (int)column[1]!);
        Assert.Null(column[2]);
    }
}
