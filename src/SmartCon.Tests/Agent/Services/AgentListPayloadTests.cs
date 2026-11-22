using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentListPayloadTests
{
    private static readonly string[] Available = { "familyId", "name", "category", "typeCount", "isEditable" };

    private static readonly string[] Default = { "familyId", "name" };

    [Fact]
    public void SelectFields_NoRequest_UsesDefaultSet()
    {
        var selection = AgentListPayload.SelectFields(Available, null, Default);

        Assert.Equal(Default, selection.Fields);
        Assert.Equal(Available, selection.AvailableFields);
        Assert.True(selection.IsDefault);
        Assert.True(selection.IsValid);
        Assert.Empty(selection.UnknownFields);
        Assert.Null(selection.Error);
    }

    [Fact]
    public void SelectFields_BlankEntriesOnly_UsesDefaultSet()
    {
        var selection = AgentListPayload.SelectFields(Available, new[] { " ", "" }, Default);

        Assert.True(selection.IsDefault);
        Assert.Equal(Default, selection.Fields);
        Assert.True(selection.IsValid);
    }

    [Fact]
    public void SelectFields_UnknownNames_ReportErrorListingAllowedNames()
    {
        var selection = AgentListPayload.SelectFields(
            Available, new[] { "name", "colour", "Size" }, Default);

        Assert.False(selection.IsValid);
        Assert.Equal(new[] { "colour", "Size" }, selection.UnknownFields);
        Assert.Equal(
            "Unknown field(s): colour, Size. Allowed fields: familyId, name, category, typeCount, isEditable.",
            selection.Error);
    }

    [Fact]
    public void SelectFields_UnknownNameTwice_IsReportedOnce()
    {
        var selection = AgentListPayload.SelectFields(Available, new[] { "colour", "Colour" }, Default);

        Assert.Equal(new[] { "colour" }, selection.UnknownFields);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("NAME")]
    [InlineData("  Name  ")]
    public void SelectFields_CaseInsensitiveMatch_ReturnsCanonicalName(string requested)
    {
        var selection = AgentListPayload.SelectFields(Available, new[] { requested }, Default);

        Assert.True(selection.IsValid);
        Assert.False(selection.IsDefault);
        Assert.Equal(new[] { "name" }, selection.Fields);
    }

    [Fact]
    public void SelectFields_DuplicatesAndOrder_KeepFirstOccurrenceInRequestedOrder()
    {
        var selection = AgentListPayload.SelectFields(
            Available, new[] { "typeCount", "name", "TYPECOUNT", "name" }, Default);

        Assert.Equal(new[] { "typeCount", "name" }, selection.Fields);
    }

    [Fact]
    public void SelectFields_MixedKnownAndUnknown_StillResolvesTheKnownOnes()
    {
        var selection = AgentListPayload.SelectFields(
            Available, new[] { "name", "colour" }, Default);

        Assert.False(selection.IsValid);
        Assert.Equal(new[] { "name" }, selection.Fields);
        Assert.Equal(new[] { "colour" }, selection.UnknownFields);
    }

    [Fact]
    public void Project_ReturnsOnlySelectedFieldsInSelectionOrder()
    {
        var record = Record();
        var selection = AgentListPayload.SelectFields(Available, new[] { "typeCount", "name" }, Default);

        var projected = AgentListPayload.Project(record, selection);

        Assert.Equal(new[] { "typeCount", "name" }, projected.Keys);
        Assert.Equal(2, projected["typeCount"]);
        Assert.Equal("A", projected["name"]);
    }

    [Fact]
    public void Project_NoRequest_ReturnsDefaultFieldsOnly()
    {
        var selection = AgentListPayload.SelectFields(Available, null, Default);

        var projected = AgentListPayload.Project(Record(), selection);

        Assert.Equal(new[] { "familyId", "name" }, projected.Keys);
        Assert.Equal(7L, projected["familyId"]);
    }

    [Fact]
    public void Project_FieldMissingInRecord_IsOmittedNotNulled()
    {
        var record = new Dictionary<string, object?> { ["familyId"] = 7L, ["name"] = "A" };
        var selection = AgentListPayload.SelectFields(Available, new[] { "isEditable", "name" }, Default);

        var projected = AgentListPayload.Project(record, selection);

        Assert.Equal(new[] { "name" }, projected.Keys);
        Assert.False(projected.ContainsKey("isEditable"));
    }

    [Theory]
    [InlineData(10, 0, 10, null, false)]
    [InlineData(10, 0, 5, 5, true)]
    [InlineData(10, 5, 5, null, false)]
    [InlineData(10, 5, 3, 8, true)]
    [InlineData(0, 0, 0, null, false)]
    [InlineData(10, 20, 0, null, false)]
    public void BuildPage_SetsNextOffsetOnlyWhenRecordsRemain(
        int total, int offset, int returned, int? expectedNext, bool expectedTruncated)
    {
        var page = AgentListPayload.BuildPage(total, offset, returned);

        Assert.Equal(total, page.Total);
        Assert.Equal(returned, page.Returned);
        Assert.Equal(offset, page.Offset);
        Assert.Equal(expectedNext, page.NextOffset);
        Assert.Equal(expectedTruncated, page.Truncated);
    }

    [Fact]
    public void BuildPage_AddTo_WritesTheFivePagingFields()
    {
        var payload = new Dictionary<string, object?> { ["families"] = new List<string>() };

        AgentListPayload.BuildPage(12, 2, 4).AddTo(payload);

        Assert.Equal(12, payload["total"]);
        Assert.Equal(4, payload["returned"]);
        Assert.Equal(2, payload["offset"]);
        Assert.Equal(6, payload["nextOffset"]);
        Assert.Equal(true, payload["truncated"]);
    }

    [Fact]
    public void AddFieldInfo_WritesFieldsAndAvailableFields()
    {
        var payload = new Dictionary<string, object?>();

        AgentListPayload.AddFieldInfo(payload, Default, Available);

        Assert.Same(Default, payload["fields"]);
        Assert.Same(Available, payload["availableFields"]);
    }

    [Fact]
    public void TruncatedHint_ThreeWays_JoinsTheLastWithOr()
    {
        var hint = AgentListPayload.TruncatedHint(new[]
        {
            "category / nameContains", "request fewer fields", "pass summary=true for counts"
        });

        Assert.Equal(
            " Narrow with category / nameContains, request fewer fields, or pass summary=true for counts.",
            hint);
    }

    [Theory]
    [InlineData(new[] { "typeIds" }, " Narrow with typeIds.")]
    [InlineData(new[] { "typeIds", "fewer fields" }, " Narrow with typeIds or fewer fields.")]
    [InlineData(new string[0], "")]
    public void TruncatedHint_OneTwoOrNoWays(string[] ways, string expected)
    {
        Assert.Equal(expected, AgentListPayload.TruncatedHint(ways));
    }

    [Fact]
    public void TruncatedHint_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AgentListPayload.TruncatedHint(null!));
    }

    [Fact]
    public void CompactFieldsHint_ListsTheFieldsAndHowToAskForOthers()
    {
        var hint = AgentListPayload.CompactFieldsHint(
            new[] { "familyId", "name", "category", "typeCount" });

        Assert.Equal(
            " Compact records show familyId, name, category, typeCount; " +
            "pass fields=[...] for other fields (availableFields).",
            hint);
    }

    [Fact]
    public void CompactFieldsHint_WithMore_AppendsTheExtraSentence()
    {
        var hint = AgentListPayload.CompactFieldsHint(
            new[] { "typeId", "name" }, " Pass parameterNames=[...] to add parameter values.");

        Assert.Equal(
            " Compact records show typeId, name; pass fields=[...] for other fields " +
            "(availableFields). Pass parameterNames=[...] to add parameter values.",
            hint);
    }

    [Fact]
    public void CompactFieldsHint_NoFields_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AgentListPayload.CompactFieldsHint(Array.Empty<string>()));
    }

    [Fact]
    public void CompactHintThreshold_IsFiftyRecords()
    {
        Assert.Equal(50, AgentListPayload.CompactHintThreshold);
    }

    private static Dictionary<string, object?> Record() => new()
    {
        ["familyId"] = 7L,
        ["name"] = "A",
        ["category"] = "C",
        ["typeCount"] = 2,
        ["isEditable"] = true
    };
}
