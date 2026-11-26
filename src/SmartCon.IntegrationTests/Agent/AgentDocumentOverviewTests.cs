using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// documentKind / isDetached (S.44): after switching to a family document the
/// overview must say it is a family (Family Editor), not just show "Вид 1".
/// </summary>
public sealed class AgentDocumentOverviewTests : RevitApiTest
{
    [Test]
    public async Task Overview_ProjectDocument_ReportsProjectKind()
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var result = new GetProjectOverviewTool(new StubRevitContext(doc))
                .ExecuteForTests(doc, Parse("{}"), CancellationToken.None);

            await Assert.That(result.Success).IsTrue();
            var data = result.Data!.Value;
            await Assert.That(data.GetProperty("documentKind").GetString()).IsEqualTo("project");
            await Assert.That(data.GetProperty("isDetached").GetBoolean()).IsFalse();
        }
        finally
        {
            doc.Close(false);
        }
    }

    [Test]
    public async Task Overview_FamilyDocument_ReportsFamilyKind()
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
            Skip.Test("В установленном Revit нет family-шаблона (.rft)");
        var doc = Application.NewFamilyDocument(template!);
        if (doc is null)
            Skip.Test("Revit не создал документ семейства");
        try
        {
            var result = new GetProjectOverviewTool(new StubRevitContext(doc!))
                .ExecuteForTests(doc!, Parse("{}"), CancellationToken.None);

            await Assert.That(result.Success).IsTrue();
            var data = result.Data!.Value;
            await Assert.That(data.GetProperty("documentKind").GetString()).IsEqualTo("family");
            await Assert.That(data.GetProperty("isDetached").GetBoolean()).IsFalse();
            await Assert.That(result.Message ?? string.Empty).Contains("Family Editor");
        }
        finally
        {
            doc!.Close(false);
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
