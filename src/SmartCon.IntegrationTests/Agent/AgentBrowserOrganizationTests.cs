using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// get_browser_organization read coverage (#350): the default template carries
/// at least one saved organization per section, exactly one of them is current,
/// and the honest read-only note is part of the contract.
/// </summary>
public sealed class AgentBrowserOrganizationTests : RevitApiTest
{
    private Document? _document;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task GetBrowserOrganization_Views_ListsSchemesWithOneCurrent()
    {
        var result = Execute(@"{""target"":""views""}");

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("target").GetString()).IsEqualTo("views");

        var organizations = data.GetProperty("organizations").EnumerateArray().ToList();
        await Assert.That(organizations.Count).IsGreaterThan(0);

        var currentCount = organizations.Count(org => org.GetProperty("isCurrent").GetBoolean());
        await Assert.That(currentCount).IsEqualTo(1);

        var current = data.GetProperty("current");
        await Assert.That(current.GetProperty("name").GetString()).IsNotNull();
        await Assert.That(current.GetProperty("id").GetInt64()).IsGreaterThan(0);

        await Assert.That(data.GetProperty("writableByApi").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("note").GetString()!.Contains("Read-only"))
            .IsTrue();

        var details = data.GetProperty("currentDetails");
        await Assert.That(details.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(details.GetProperty("sampleFolders").ValueKind)
            .IsEqualTo(JsonValueKind.Array);
    }

    [Test]
    public async Task GetBrowserOrganization_Sheets_CurrentMatchesListedScheme()
    {
        var result = Execute(@"{""target"":""sheets""}");

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("target").GetString()).IsEqualTo("sheets");

        var organizations = data.GetProperty("organizations").EnumerateArray().ToList();
        await Assert.That(organizations.Count).IsGreaterThan(0);
        await Assert.That(
                organizations.Count(org => org.GetProperty("isCurrent").GetBoolean()))
            .IsEqualTo(1);

        var currentId = data.GetProperty("current").GetProperty("id").GetInt64();
        await Assert.That(
                organizations.Any(org =>
                    org.GetProperty("id").GetInt64() == currentId
                    && org.GetProperty("isCurrent").GetBoolean()))
            .IsTrue();
    }

    [Test]
    public async Task GetBrowserOrganization_UnknownTarget_TypedFail()
    {
        var result = Execute(@"{""target"":""legends""}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_target");
    }

    private ToolResult Execute(string json)
    {
        var tool = new GetBrowserOrganizationTool(new StubRevitContext(Doc));
        using var document = JsonDocument.Parse(json);
        return tool.ExecuteForTests(Doc, document.RootElement.Clone(), CancellationToken.None);
    }
}
