using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// #222: per-type value diff between two catalog versions
/// (<see cref="CatalogVersionTypeDiffLogic"/>).
/// </summary>
public sealed class CatalogVersionTypeDiffLogicTests
{
    private static ExtractedAttributeValue Value(
        string typeId, string param, string? text = null, string? raw = null, double? number = null) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            CatalogItemId: "item",
            VersionId: "ver",
            FileId: null,
            TypeId: typeId,
            AttributeId: null,
            BindingId: null,
            ParameterName: param,
            ParameterScope: AttributeScope.Type,
            StorageType: "String",
            ValueText: text,
            ValueRaw: raw,
            ValueNumber: number,
            UnitTypeId: null,
            Status: AttributeValueStatus.Found,
            Message: null,
            ExtractionRunId: "run",
            ExtractedAtUtc: DateTimeOffset.UnixEpoch);

    private static IReadOnlyDictionary<string, string> Names(params (string Id, string Name)[] types) =>
        types.ToDictionary(t => t.Id, t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void ChangedValue_TypeReported()
    {
        var from = new[] { Value("t1", "Модель", "СТАРАЯ") };
        var to = new[] { Value("t1", "Модель", "НОВАЯ") };
        var names = Names(("t1", "100"));

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(from, names, to, names);

        Assert.Equal(["100"], changed);
    }

    [Fact]
    public void SameValues_NoChanges()
    {
        var from = new[] { Value("t1", "Модель", "A"), Value("t2", "Модель", "B") };
        var to = new[] { Value("t1", "Модель", "A"), Value("t2", "Модель", "B") };
        var names = Names(("t1", "100"), ("t2", "200"));

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(from, names, to, names);

        Assert.Empty(changed);
    }

    [Fact]
    public void AddedParameter_TypeReported()
    {
        var from = Array.Empty<ExtractedAttributeValue>();
        var to = new[] { Value("t1", "Модель", "A") };
        var names = Names(("t1", "100"));

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(from, names, to, names);

        Assert.Equal(["100"], changed);
    }

    [Fact]
    public void RemovedParameter_TypeReported()
    {
        var from = new[] { Value("t1", "Модель", "A") };
        var to = Array.Empty<ExtractedAttributeValue>();
        var names = Names(("t1", "100"));

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(from, names, to, names);

        Assert.Equal(["100"], changed);
    }

    [Fact]
    public void ChangedOnlyInOneType_OtherTypeNotReported()
    {
        // The #222 driver scenario: the edit landed in type 300 while the
        // project has type 100 loaded — 100 must NOT appear in the diff.
        var from = new[] { Value("t1", "Модель", ""), Value("t3", "Модель", "") };
        var to = new[] { Value("t1", "Модель", ""), Value("t3", "Модель", "НОВАЯ") };
        var names = Names(("t1", "100"), ("t3", "300"));

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(from, names, to, names);

        Assert.Equal(["300"], changed);
    }

    [Fact]
    public void FamilyLevelRows_AreSkipped()
    {
        var familyLevel = new ExtractedAttributeValue(
            Id: "x", CatalogItemId: "item", VersionId: "ver", FileId: null,
            TypeId: null, AttributeId: null, BindingId: null,
            ParameterName: "P", ParameterScope: AttributeScope.Instance,
            StorageType: "String", ValueText: "1", ValueRaw: null, ValueNumber: null,
            UnitTypeId: null, Status: AttributeValueStatus.Found, Message: null,
            ExtractionRunId: "run", ExtractedAtUtc: DateTimeOffset.UnixEpoch);

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(
            [familyLevel], Names(), [], Names());

        Assert.Empty(changed);
    }

    [Fact]
    public void ValueFallbacks_RawAndNumber()
    {
        var from = new[] { Value("t1", "A", raw: "r1"), Value("t2", "B", number: 1.5) };
        var to = new[] { Value("t1", "A", raw: "r2"), Value("t2", "B", number: 1.5) };
        var names = Names(("t1", "100"), ("t2", "200"));

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(from, names, to, names);

        Assert.Equal(["100"], changed);
    }

    [Fact]
    public void Result_IsSortedOrdinal()
    {
        var from = new[] { Value("t1", "P", "1"), Value("t2", "P", "1"), Value("t3", "P", "1") };
        var to = new[] { Value("t1", "P", "2"), Value("t2", "P", "2"), Value("t3", "P", "2") };
        var names = Names(("t1", "300"), ("t2", "100"), ("t3", "200"));

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(from, names, to, names);

        Assert.Equal(["100", "200", "300"], changed);
    }
}
