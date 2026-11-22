using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// summarize_elements on the shared query engine: the typed unknown_parameter
/// failure and the family / typeId / category identity of type and family
/// groups. Partial of <see cref="AgentElementQueryHonestyTests"/>.
/// </summary>
public sealed partial class AgentElementQueryHonestyTests
{
    [Test]
    public async Task SummarizeElements_ParameterMissingOnEveryElement_ReturnsUnknownParameter()
    {
        RequireWall();

        var result = Summarize(
            "{\"categories\":[\"OST_Walls\"],\"groupBy\":\"category\",\"conditions\":[" +
            Condition("TypoZzz", "eq", "1") + "]}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_parameter");
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("index").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("scanned").GetInt32()).IsGreaterThan(0);
        await Assert.That(data.GetProperty("suggestions").GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task SummarizeElements_GroupByType_CarriesFamilyTypeAndCategory()
    {
        RequireWall();
        var wall = WallElement;
        var type = WallTypeOf();

        var walls = Summarize("{\"categories\":[\"OST_Walls\"],\"groupBy\":\"type\"}");
        await Assert.That(walls.Success).IsTrue();
        var groups = Groups(walls);
        var wallGroup = groups.Single(item => item.GetProperty("key").GetString() == type.Name);
        await Assert.That(wallGroup.GetProperty("family").GetString()).IsEqualTo(type.FamilyName);
        await Assert.That(wallGroup.GetProperty("typeId").GetInt64()).IsEqualTo(type.Id.GetValue());
        await Assert.That(wallGroup.GetProperty("category").GetString()).IsEqualTo(wall.Category?.Name);
        await Assert.That(wallGroup.GetProperty("count").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(walls.Data!.Value.GetProperty("groupsTotal").GetInt32()).IsEqualTo(groups.Count);

        var instanceId = PlaceFamilyInstance();
        var instance = (FamilyInstance)Doc.GetElement(ElementIdCompat.Create(instanceId));
        var instances = Summarize(
            "{\"categories\":[" + Json(instance.Category?.Name ?? string.Empty) + "],\"groupBy\":\"type\"}");
        await Assert.That(instances.Success).IsTrue();
        var instanceGroup = Groups(instances).Single(item =>
            item.GetProperty("key").GetString() == instance.Symbol?.Name);
        await Assert.That(instanceGroup.GetProperty("family").GetString()).IsEqualTo(FamilyName);
        await Assert.That(instanceGroup.GetProperty("typeId").GetInt64())
            .IsEqualTo(instance.Symbol!.Id.GetValue());
        await Assert.That(instanceGroup.GetProperty("count").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task SummarizeElements_GroupByFamily_CountsPerFamilyName()
    {
        RequireWall();
        var instanceId = PlaceFamilyInstance();
        var instance = (FamilyInstance)Doc.GetElement(ElementIdCompat.Create(instanceId));
        var wallFamily = WallTypeOf().FamilyName;

        var result = Summarize(
            "{\"categories\":[\"OST_Walls\"," + Json(instance.Category?.Name ?? string.Empty) +
            "],\"groupBy\":\"family\"}");

        await Assert.That(result.Success).IsTrue();
        var groups = Groups(result).ToDictionary(item => item.GetProperty("key").GetString()!);
        await Assert.That(groups[FamilyName].GetProperty("family").GetString()).IsEqualTo(FamilyName);
        await Assert.That(groups[FamilyName].GetProperty("count").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(groups[FamilyName].TryGetProperty("typeId", out _)).IsFalse();
        await Assert.That(groups[wallFamily!].GetProperty("family").GetString()).IsEqualTo(wallFamily);
        await Assert.That(groups[wallFamily!].TryGetProperty("typeId", out _)).IsFalse();
        await Assert.That(result.Data!.Value.GetProperty("groupsTotal").GetInt32()).IsEqualTo(groups.Count);
    }

    [Test]
    public async Task SummarizeElements_GroupByFamily_ElementsWithoutFamilyFormOneGroup()
    {
        RequireWall();
        RunInTransaction("Create model line", doc =>
        {
            // A model line has a line style rather than a family type.
            var sketchPlane = SketchPlane.Create(
                doc,
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
            doc.Create.NewModelCurve(
                Line.CreateBound(new XYZ(0, 40, 0), new XYZ(10, 40, 0)),
                sketchPlane);
        });

        var result = Summarize("{\"categories\":[\"OST_Lines\"],\"groupBy\":\"family\"}");

        await Assert.That(result.Success).IsTrue();
        var noFamily = Groups(result).Single(item => item.GetProperty("key").GetString() == "(no family)");
        await Assert.That(noFamily.GetProperty("count").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(noFamily.TryGetProperty("family", out _)).IsFalse();
        await Assert.That(noFamily.TryGetProperty("typeId", out _)).IsFalse();
    }

    [Test]
    public async Task SummarizeElements_SameTypeNameInTwoCategories_DoesNotClaimOneIdentity()
    {
        RequireWall();
        if (!ModelSeed.HasPipeTypes(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет типов труб");

        const string sharedName = "SC_AgentQueryHonesty_Same";
        var ownTypeName = WallTypeOf().Name ?? string.Empty;
        try
        {
            RunInTransaction("Seed same-named types in two categories", doc =>
            {
                var levelId = ElementIdCompat.Create(_levelId);
                var wallType = (ElementType)doc.GetElement(
                    doc.GetElement(ElementIdCompat.Create(_wallId)).GetTypeId());
                var pipeType = (ElementType)doc.GetElement(
                    new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId());
                var systemTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystemType))
                    .FirstElementId();

                var wallLine = Line.CreateBound(new XYZ(0, 20, 0), new XYZ(10, 20, 0));
                Wall.Create(doc, wallLine, wallType.Duplicate(sharedName).Id, levelId, 10.0, 0.0, false, false);
                Pipe.Create(
                    doc,
                    systemTypeId,
                    pipeType.Duplicate(sharedName).Id,
                    levelId,
                    new XYZ(0, 30, 0),
                    new XYZ(10, 30, 0));
            });
        }
        catch (Exception ex)
        {
            Skip.Test("Revit не дублирует типы в этом шаблоне: " + ex.Message);
            return;
        }

        var result = Summarize(
            "{\"categories\":[\"OST_Walls\",\"OST_PipeCurves\"],\"groupBy\":\"type\",\"topN\":200}");
        await Assert.That(result.Success).IsTrue();

        // One group by name, two types behind it: one identity would be wrong for
        // half of the count, so none is claimed.
        var merged = Groups(result).Single(item => item.GetProperty("key").GetString() == sharedName);
        await Assert.That(merged.GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(merged.TryGetProperty("family", out _)).IsFalse();
        await Assert.That(merged.TryGetProperty("typeId", out _)).IsFalse();
        await Assert.That(merged.TryGetProperty("category", out _)).IsFalse();

        var own = Groups(result).Single(item => item.GetProperty("key").GetString() == ownTypeName);
        await Assert.That(own.GetProperty("family").GetString()).IsEqualTo(WallTypeOf().FamilyName);
        await Assert.That(own.GetProperty("typeId").GetInt64()).IsEqualTo(WallTypeOf().Id.GetValue());
        await Assert.That(own.GetProperty("category").GetString())
            .IsEqualTo(WallElement.Category?.Name);
    }

    [Test]
    public async Task SummarizeElements_GroupByParameter_AcceptsStructuralField()
    {
        RequireWall();
        var typeName = WallTypeOf().Name ?? string.Empty;

        var byType = Summarize("{\"categories\":[\"OST_Walls\"],\"groupBy\":\"parameter\",\"parameter\":\"type\"}");
        await Assert.That(byType.Success).IsTrue();
        await Assert.That(
                Groups(byType).Any(item => item.GetProperty("key").GetString() == typeName))
            .IsTrue();

        // A group parameter that exists nowhere keeps its typed failure, and the
        // suggestion pool now includes the structural fields.
        var unknown = Summarize(
            "{\"categories\":[\"OST_Walls\"],\"groupBy\":\"parameter\",\"parameter\":\"typ\"}");
        await Assert.That(unknown.Success).IsFalse();
        await Assert.That(unknown.ErrorCode).IsEqualTo("unknown_parameter");
        await Assert.That(Strings(unknown.Data!.Value.GetProperty("suggestions")).Contains("type")).IsTrue();
    }
}
