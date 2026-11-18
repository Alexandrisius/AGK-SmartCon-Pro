using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent.Eval;

/// <summary>
/// Deterministic model for the eval harness: fixed names, stable structure.
/// A fresh document is seeded per task so tasks cannot influence each other and
/// replay fixtures always find the same objects.
/// </summary>
internal sealed class EvalModelSeed
{
    public const string Level1Name = "SC_Eval_L1";
    public const string Level2Name = "SC_Eval_L2";
    public const string Level3Name = "SC_Eval_L3";
    public const string RefViewName = "SC_Eval_Ref";
    public const string TargetAName = "SC_Eval_A";
    public const string TargetBName = "SC_Eval_B";
    public const string TargetCName = "SC_Eval_C";
    public const string TemplateName = "SC_Eval_Tpl";
    public const string WallComment = "SC_Eval_Wall";

    private EvalModelSeed()
    {
    }

    public long Level1Id { get; private init; }

    public long Level2Id { get; private init; }

    public long Level3Id { get; private init; }

    public long RefViewId { get; private init; }

    public long TargetAId { get; private init; }

    public long TargetBId { get; private init; }

    public long TargetCId { get; private init; }

    public long TemplateId { get; private init; }

    public long WallId { get; private init; }

    public IReadOnlyList<long> WallIds { get; private init; } = Array.Empty<long>();

    public string WallTypeName { get; private init; } = string.Empty;

    public string WallTypePrefix { get; private init; } = string.Empty;

    public string DocumentId { get; private init; } = string.Empty;

    public bool WallsCreated { get; private init; }

    public static EvalModelSeed? Create(Document document, RevitTransactionService transactions)
    {
        if (!ModelSeed.HasFloorPlanViewType(document))
            return null;

        var result = new EvalModelSeed();

        transactions.RunInTransaction(document, "Seed agent eval model", doc =>
        {
            var level1 = ModelSeed.CreateLevel(doc, 0.0);
            level1.Name = Level1Name;
            var level2 = ModelSeed.CreateLevel(doc, 3.0);
            level2.Name = Level2Name;
            var level3 = ModelSeed.CreateLevel(doc, 6.0);
            level3.Name = Level3Name;

            var refView = ModelSeed.CreateViewPlanChecked(doc, level1.Id, RefViewName);
            var targetA = ModelSeed.CreateViewPlanChecked(doc, level1.Id, TargetAName);
            var targetB = ModelSeed.CreateViewPlanChecked(doc, level2.Id, TargetBName);
            var targetC = ModelSeed.CreateViewPlanChecked(doc, level3.Id, TargetCName);

            var (wallIds, wallTypeName) = CreateWalls(doc, level1.Id);

            refView.CropBoxActive = true;
            refView.CropBoxVisible = true;
            refView.CropBox = new BoundingBoxXYZ
            {
                Min = new XYZ(-5, -5, 0),
                Max = new XYZ(5, 5, 10),
                Transform = Transform.Identity
            };

            var template = refView.CreateViewTemplate();
            template.Name = TemplateName;
            refView.ViewTemplateId = template.Id;

            result = new EvalModelSeed
            {
                Level1Id = level1.Id.GetValue(),
                Level2Id = level2.Id.GetValue(),
                Level3Id = level3.Id.GetValue(),
                RefViewId = refView.Id.GetValue(),
                TargetAId = targetA.Id.GetValue(),
                TargetBId = targetB.Id.GetValue(),
                TargetCId = targetC.Id.GetValue(),
                TemplateId = template.Id.GetValue(),
                WallId = wallIds.Count == 0 ? 0 : wallIds[0],
                WallIds = wallIds,
                WallTypeName = wallTypeName,
                WallTypePrefix = wallTypeName.Length <= 4
                    ? wallTypeName
                    : wallTypeName.Substring(0, 4),
                WallsCreated = wallIds.Count > 0,
                DocumentId = AgentDocumentIdentity.GetDocumentId(doc)
            };
        });

        return result;
    }

    private static (List<long> Ids, string TypeName) CreateWalls(
        Document document,
        ElementId levelId)
    {
        var ids = new List<long>();
        var wallTypeId = new FilteredElementCollector(document)
            .OfClass(typeof(WallType))
            .WhereElementIsElementType()
            .FirstElementId();
        if (wallTypeId == ElementId.InvalidElementId)
            return (ids, string.Empty);

        var typeName = document.GetElement(wallTypeId)?.Name ?? string.Empty;
        for (var index = 0; index < 3; index++)
        {
            var y = index * 3.0;
            var line = Line.CreateBound(new XYZ(0, y, 0), new XYZ(10, y, 0));
            var wall = Wall.Create(
                document, line, wallTypeId, levelId, 10.0, 0.0, false, false);
            wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.Set(WallComment);
            ids.Add(wall.Id.GetValue());
        }

        return (ids, typeName);
    }
}
