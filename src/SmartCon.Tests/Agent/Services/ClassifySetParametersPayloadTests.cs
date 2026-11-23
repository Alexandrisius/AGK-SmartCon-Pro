using System.Text.Json;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The setParameters argument of classify_types must be EXACTLY what the
/// set_parameters schema accepts (R7): {items} for one call of at most 500
/// items, {calls:[{items}…]} above 500 - set_parameters has no batch argument,
/// so the model makes one call per entry. The schema itself is validated
/// end-to-end by the integration test against the tool definition; this guard
/// pins the shape the Core result produces.
/// </summary>
public sealed class ClassifySetParametersPayloadTests
{
    [Fact]
    public void SingleBatch_IsItemsOnly()
    {
        var result = Result(Batch(3));

        var payload = JsonSerializer.SerializeToElement(result.BuildSetParametersPayload()!);

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(new[] { "items" }, payload.EnumerateObject().Select(property => property.Name));
        Assert.Equal(3, payload.GetProperty("items").GetArrayLength());
        Assert.All(
            payload.GetProperty("items").EnumerateArray(),
            item => Assert.Equal(
                new[] { "elementId", "parameter", "value", "scope" },
                item.EnumerateObject().Select(property => property.Name)));
    }

    [Fact]
    public void ManyBatches_IsBatchesOfItemsOnly()
    {
        var result = Result(Batch(500), Batch(120));

        var payload = JsonSerializer.SerializeToElement(result.BuildSetParametersPayload()!);

        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(new[] { "calls" }, payload.EnumerateObject().Select(property => property.Name));
        var batches = payload.GetProperty("calls");
        Assert.Equal(JsonValueKind.Array, batches.ValueKind);
        Assert.Equal(2, batches.GetArrayLength());
        Assert.All(
            batches.EnumerateArray(),
            batch =>
            {
                Assert.Equal(JsonValueKind.Object, batch.ValueKind);
                Assert.Equal(
                    new[] { "items" },
                    batch.EnumerateObject().Select(property => property.Name));
                Assert.InRange(batch.GetProperty("items").GetArrayLength(), 1, 500);
            });
    }

    [Fact]
    public void NoProposedItems_IsNull()
    {
        var result = new ClassifyMatchResult();

        Assert.Null(result.BuildSetParametersPayload());
    }

    private static ClassifyMatchResult Result(params IReadOnlyList<ClassifySetParameterItem>[] batches) =>
        new()
        {
            SetParameterBatches = batches
        };

    private static ClassifySetParameterItem[] Batch(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new ClassifySetParameterItem(
                index, AgentClassificationMatcher.AssemblyCodeParameter, "10.10.10", "type"))
            .ToArray();
}
