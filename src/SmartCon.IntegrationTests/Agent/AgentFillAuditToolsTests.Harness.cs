using System.Text.Json;
using Nice3point.TUnit.Revit;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Group/find harness feedback (S.33/S.35): compact groups, prefix aggregation
/// and the prefix summary of the family audit.
/// </summary>
public sealed partial class AgentFillAuditToolsTests
{
    [Test]
    public async Task GroupFamiliesBySharedParameters_CompactMode_KeepsNamesAndFamilyIds()
    {
        if (!_familiesSeeded)
            Skip.Test("Сид-семейства не собрались (нет .rft шаблона или ФОП)");

        var tool = new GroupFamiliesBySharedParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scope\":\"type\",\"compact\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("compact").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("familiesWithoutSharedParameters").GetInt32())
            .IsGreaterThanOrEqualTo(0);

        var groupA = data.GetProperty("groups").EnumerateArray().First(group =>
            group.GetProperty("parameterNames").EnumerateArray()
                .Any(name => name.GetString() == "СК_Группа_А"));
        await Assert.That(groupA.GetProperty("parameterCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(groupA.GetProperty("familyCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(groupA.GetProperty("familyIds").GetArrayLength())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(groupA.GetProperty("familyIdsReturned").GetInt32())
            .IsEqualTo(groupA.GetProperty("familyIds").GetArrayLength());
        if (!groupA.GetProperty("familiesTruncated").GetBoolean())
            await Assert.That(groupA.GetProperty("familyIdsNote").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
        await Assert.That(groupA.TryGetProperty("parameters", out _)).IsFalse();
        await Assert.That(groupA.TryGetProperty("families", out _)).IsFalse();

        var unplaced = data.GetProperty("unplacedFamilies");
        await Assert.That(unplaced.ValueKind).IsEqualTo(JsonValueKind.Null);
        if (data.GetProperty("unplacedCount").GetInt32() > 0)
        {
            await Assert.That(data.GetProperty("unplacedFamiliesSuppressed").GetBoolean())
                .IsTrue();
        }
    }

    [Test]
    public async Task GroupFamiliesBySharedParameters_PrefixMode_CountsSourceBuckets()
    {
        if (!_familiesSeeded)
            Skip.Test("Сид-семейства не собрались (нет .rft шаблона или ФОП)");

        var tool = new GroupFamiliesBySharedParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scope\":\"type\",\"groupBy\":\"prefix\",\"compact\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("groupBy").GetString()).IsEqualTo("prefix");

        var prefixGroup = data.GetProperty("groups").EnumerateArray().First(group =>
            group.GetProperty("prefix").GetString() == "СК_");
        await Assert.That(prefixGroup.GetProperty("familyCount").GetInt32())
            .IsGreaterThanOrEqualTo(2);
        await Assert.That(prefixGroup.GetProperty("parameterNames").EnumerateArray()
                .Any(name => name.GetString() == "СК_Группа_А"))
            .IsTrue();
        await Assert.That(prefixGroup.GetProperty("familyIds").GetArrayLength())
            .IsGreaterThanOrEqualTo(2);
        await Assert.That(prefixGroup.GetProperty("familyIdsReturned").GetInt32())
            .IsEqualTo(prefixGroup.GetProperty("familyIds").GetArrayLength());
        await Assert.That(prefixGroup.TryGetProperty("signatureDigest", out _)).IsFalse();

        var paged = tool.ExecuteForTests(
            Doc,
            Parse("{\"scope\":\"type\",\"groupBy\":\"prefix\",\"compact\":true," +
                  "\"familyIdsOffset\":1,\"familyIdsLimit\":1}"),
            CancellationToken.None);
        await Assert.That(paged.Success).IsTrue();
        var pagedGroup = paged.Data!.Value.GetProperty("groups").EnumerateArray().First(group =>
            group.GetProperty("prefix").GetString() == "СК_");
        await Assert.That(pagedGroup.GetProperty("familyIdsOffset").GetInt32()).IsEqualTo(1);
        await Assert.That(pagedGroup.GetProperty("familyIds").GetArrayLength()).IsEqualTo(1);
        await Assert.That(pagedGroup.GetProperty("familyIdsReturned").GetInt32()).IsEqualTo(1);
        await Assert.That(pagedGroup.GetProperty("familyIdsNote").GetString())
            .Contains("offset 1");
    }

    [Test]
    public async Task FindFamiliesWithParameter_PrefixSummary_ReportsSharedPrefixes()
    {
        if (!_familiesSeeded)
            Skip.Test("Сид-семейства не собрались (нет .rft шаблона или ФОП)");

        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"СК_Группа_\",\"match\":\"prefix\"," +
                  "\"includeInstanceParameters\":true,\"compact\":true,\"summary\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(2);
        await Assert.That(data.GetProperty("familiesWithoutAnyShared").GetInt32())
            .IsGreaterThanOrEqualTo(0);
        await Assert.That(data.GetProperty("unplacedCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(data.GetProperty("summaryScope").GetString())
            .IsEqualTo("type+instance");

        var categoryTotal = 0;
        foreach (var row in data.GetProperty("byCategory").EnumerateArray())
            categoryTotal += row.GetProperty("families").GetInt32();
        await Assert.That(categoryTotal).IsEqualTo(data.GetProperty("total").GetInt32());

        var prefix = data.GetProperty("byPrefix").EnumerateArray().First(row =>
            row.GetProperty("prefix").GetString() == "СК_");
        await Assert.That(prefix.GetProperty("families").GetInt32())
            .IsGreaterThanOrEqualTo(2);

        var family = data.GetProperty("families").EnumerateArray().First();
        await Assert.That(string.IsNullOrEmpty(
            family.GetProperty("familyName").GetString())).IsFalse();
        await Assert.That(family.GetProperty("matchedParameterCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(family.GetProperty("sample").GetArrayLength())
            .IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task FindFamiliesWithParameter_DescriptionDistinguishesCounters()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));
        var description = tool.Definition.Description;

        await Assert.That(description)
            .Contains("matchedParameterCount counts distinct matched parameter NAMES");
        await Assert.That(description)
            .Contains("matchedTypeCount counts type-level matches");
    }
}
