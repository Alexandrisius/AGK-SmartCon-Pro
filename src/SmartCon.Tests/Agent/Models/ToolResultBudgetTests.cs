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
}
