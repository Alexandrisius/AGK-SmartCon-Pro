using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// get_family_labels (v15): the label inventory of annotation/tag families.
/// The label→parameter link itself is not exposed by the API before Revit
/// 2027 (CF-3799) — the tool must state that boundary instead of guessing.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    [Test]
    public async Task GetFamilyLabels_ModelFamily_ReportsNoLabelsAndApiLimit()
    {
        var tool = new GetFamilyLabelsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"familyId\":" + Seed.FamilyId + "}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("labelCount").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("categoryType").GetString()).IsEqualTo("model");
        await Assert.That(data.GetProperty("isAnnotationFamily").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("apiLimitation").GetString() ?? string.Empty)
            .Contains("CF-3799");
        await Assert.That(result.Message ?? string.Empty).Contains("CF-3799");
    }

    [Test]
    public async Task GetFamilyLabels_AnnotationFamily_ReportsLabelsOrHonestZero()
    {
        // A bare NewProjectDocument may carry no annotation family at all:
        // skip then (the model-family path above stays the deterministic part).
        RevitFamily? annotation = null;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily candidate
                && candidate.FamilyCategory?.CategoryType == CategoryType.Annotation
                && !candidate.IsInPlace)
            {
                annotation = candidate;
                break;
            }
        }

        if (annotation is null)
        {
            Skip.Test("В тестовом документе нет аннотационных семейств (марки/титульник)");
            return;
        }

        var tool = new GetFamilyLabelsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyIds\":[" + annotation.Id.GetValue() + "]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var row = Data(result).GetProperty("families")[0];
        await Assert.That(row.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(row.GetProperty("isAnnotationFamily").GetBoolean()).IsTrue();
        await Assert.That(row.GetProperty("labels").GetArrayLength())
            .IsEqualTo(row.GetProperty("labelCount").GetInt32());
        await Assert.That(Data(result).GetProperty("annotationFamilyCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(Data(result).GetProperty("apiLimitation").GetString() ?? string.Empty)
            .Contains("CF-3799");
    }
}
