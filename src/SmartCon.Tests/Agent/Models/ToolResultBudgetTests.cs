using System.Text.Json;
using Xunit;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Models;

public sealed class ToolResultBudgetTests
{
    [Fact]
    public void Serialize_SmallResult_IsUntouched()
    {
        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { total = 1, elements = new[] { new { elementId = 1 } } }),
            "ok");

        var json = ToolResultBudget.Serialize(result);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.False(document.RootElement.GetProperty("data").TryGetProperty("truncated", out _));
    }

    [Fact]
    public void Serialize_LargeArray_IsTrimmedStructurally()
    {
        var elements = Enumerable.Range(0, 1000)
            .Select(index => new
            {
                elementId = index,
                name = "Wall number " + index + " with a reasonably long descriptive name",
                category = "OST_Walls",
                level = "Level 1"
            })
            .ToArray();

        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { total = elements.Length, elements }),
            "big");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 5000);

        Assert.True(json.Length <= 5000, "trimmed json must fit the budget, was " + json.Length);

        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("elementsTotal").GetInt32() >= 1000);
        Assert.True(data.GetProperty("elementsReturned").GetInt32() < 1000);
        Assert.Equal(
            data.GetProperty("elements").GetArrayLength(),
            data.GetProperty("elementsReturned").GetInt32());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("hint").GetString()));
    }

    [Fact]
    public void Serialize_OverBudgetFamilies_KeepsTotalAndNeverEmptiesTheArray()
    {
        // Stress-test #3: families=[] next to familiesTotal=5 read as "nothing found".
        var families = Enumerable.Range(0, 5)
            .Select(index => new
            {
                familyId = index,
                familyName = "F" + index,
                matchedTypes = Enumerable.Range(0, 120)
                    .Select(type => new { typeId = type, value = new string('x', 60) })
                    .ToArray()
            })
            .ToArray();

        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                families,
                familiesTotal = families.Length
            }),
            "found 5 families");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 4000);

        Assert.True(json.Length <= 4000);
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, data.GetProperty("familiesTotal").GetInt32());

        if (data.TryGetProperty("families", out var array))
        {
            Assert.True(
                array.GetArrayLength() > 0,
                "a truncated array must never be emptied while its total is still reported");
        }
    }

    [Fact]
    public void Serialize_NoArrays_FallsBackToDigest()
    {
        var payload = new { total = 5, padding = new string('x', 20000) };
        var result = ToolResult.Ok(JsonSerializer.SerializeToElement(payload), "huge");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 2000);

        Assert.True(json.Length <= 2000);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("data").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Serialize_KeepsSuccessAndMessage()
    {
        var elements = Enumerable.Range(0, 500).Select(i => new { id = i }).ToArray();
        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { elements }),
            "Processed 500 items");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 1000);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        var message = document.RootElement.GetProperty("message").GetString();
        Assert.StartsWith("Processed 500 items", message);
        Assert.Contains("[truncated:", message);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("elementsReturned").GetInt32() < 500);
    }

    [Fact]
    public void Trim_NonJsonInput_IsReturnedAsIs()
    {
        Assert.Equal("not json", ToolResultBudget.Trim("not json", 10));
    }

    [Fact]
    public void Serialize_TrimmedResult_CarriesTheHandleAndBlobHint()
    {
        var views = Enumerable.Range(0, 300)
            .Select(i => new { id = i, name = "План " + i })
            .ToArray();
        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { views }),
            "ok");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 2000, resultHandle: "call_1");

        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("call_1", data.GetProperty("resultHandle").GetString());
        Assert.Contains("read_tool_result", data.GetProperty("hint").GetString());
    }

    [Fact]
    public void SerializeFull_IsNotTrimmed()
    {
        var items = Enumerable.Range(0, 5000)
            .Select(i => new { id = i })
            .ToArray();
        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { items }),
            "ok");

        var full = ToolResultBudget.SerializeFull(result);

        Assert.True(full.Length > ToolResultBudget.DefaultMaxCharacters);
        Assert.DoesNotContain("truncated", full, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_FailureResult_KeepsErrorCode()
    {
        var items = Enumerable.Range(0, 300)
            .Select(i => new { id = i, reason = "duplicate name" })
            .ToArray();
        var result = ToolResult.Fail(
            "all_items_failed",
            "Nothing was renamed",
            data: JsonSerializer.SerializeToElement(new { failed = items }));

        var json = ToolResultBudget.Serialize(result, maxCharacters: 1000);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("all_items_failed", document.RootElement.GetProperty("errorCode").GetString());
        Assert.True(document.RootElement.GetProperty("data").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Serialize_LargeFamilyList_TrimsFamiliesArrayHonestly()
    {
        var families = Enumerable.Range(0, 1000)
            .Select(index => new
            {
                familyId = index + 1,
                name = "KR_SHAR_VOLK_" + index + "_with_a_long_descriptive_name",
                category = "OST_PipeAccessory",
                typeCount = 12
            })
            .ToArray();

        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { total = families.Length, families }),
            "big");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 5000);

        Assert.True(json.Length <= 5000, "trimmed json must fit the budget, was " + json.Length);

        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("familiesTotal").GetInt32() >= 1000);
        Assert.True(data.GetProperty("familiesReturned").GetInt32() < 1000);
    }

    [Fact]
    public void MaxCharactersFor_LoadToolPackGetsTheExpandedBudget()
    {
        // Pack skills are sent once per conversation: trimming them away would lose
        // the manual for the whole chat, so the pack loader gets a larger budget.
        Assert.Equal(
            ToolResultBudget.MetaMaxCharacters,
            ToolResultBudget.MaxCharactersFor("load_tool_pack"));
        Assert.Equal(
            ToolResultBudget.DefaultMaxCharacters,
            ToolResultBudget.MaxCharactersFor("list_views"));
        Assert.Equal(
            ToolResultBudget.DefaultMaxCharacters,
            ToolResultBudget.MaxCharactersFor(null));
    }

    [Fact]
    public void Serialize_OverBudget_DigestKeepsPackSkills()
    {
        // A pack skill is sent once per conversation: losing the text to the budget
        // while SentPackSkills already marked it sent would drop the manual forever.
        var cards = Enumerable.Range(0, 200)
            .Select(index => new { name = "tool_" + index, purpose = new string('x', 40) })
            .ToArray();
        var markdown = "## Пак filters\n" + new string('y', 200);
        var skills = new[] { new { pack = "filters", markdown } };

        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { cards, skills }),
            "big");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 2000);

        Assert.True(json.Length <= 2000, "trimmed json must fit the budget, was " + json.Length);

        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        var returned = data.GetProperty("skills").EnumerateArray().Single();
        Assert.Equal("filters", returned.GetProperty("pack").GetString());
        Assert.Equal(markdown, returned.GetProperty("markdown").GetString());
    }

    [Fact]
    public void Serialize_OverBudget_SchemasYieldBeforeSkills()
    {
        var schemas = Enumerable.Range(0, 8)
            .Select(index => new { name = "tool_" + index, inputSchema = new string('s', 500) })
            .ToArray();
        var markdown = "## Пак filters\n" + new string('y', 600);
        var skills = new[] { new { pack = "filters", markdown } };

        var result = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { schemas, skills }),
            "big");

        var json = ToolResultBudget.Serialize(result, maxCharacters: 3000);

        Assert.True(json.Length <= 3000, "trimmed json must fit the budget, was " + json.Length);

        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(8, data.GetProperty("schemasTotal").GetInt32());
        Assert.True(data.GetProperty("schemasReturned").GetInt32() < 8);
        Assert.Equal(markdown, data.GetProperty("skills")[0].GetProperty("markdown").GetString());
    }
}
