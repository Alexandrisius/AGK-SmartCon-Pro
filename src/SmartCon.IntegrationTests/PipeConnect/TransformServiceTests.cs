using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Math;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Transactions;
using SmartCon.Revit.Transform;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.PipeConnect;

/// <summary>
/// Граница «SmartCon ↔ Revit API»: RevitTransformService поверх реального
/// ElementTransformUtils внутри настоящей транзакции.
/// </summary>
public sealed class TransformServiceTests : RevitApiTest
{
    private const double Tolerance = 1e-6;

    // Поля без инициализаторов: инициализация полей экземпляра выполняется до
    // инъекции Revit (TUnit 1.33+), преждевременная загрузка RevitAPI ломает
    // инжектор (Nice3point/RevitUnit#78). Значения назначаются в [Before(Test)].
    private Document? _document;
    private RevitTransactionService? _transactions;
    private RevitTransformService? _transform;
    private ElementId? _wallId;

    private Document Doc => _document!;
    private RevitTransactionService Transactions => _transactions!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasWallType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет ни одного WallType — сидирование стены невозможно");
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _transform = new RevitTransformService();

        Transactions.RunInTransaction(Doc, "Seed wall", doc =>
        {
            var level = ModelSeed.CreateLevel(doc);
            var wall = ModelSeed.TryCreateWall(doc, level.Id)
                ?? throw new InvalidOperationException("WallType недоступен после проверки HasWallType");
            _wallId = wall.Id; // I-05: храним только ElementId, объект перечитываем
        });
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task MoveElement_NonZeroOffset_MovesWallLocationCurve()
    {
        // Arrange
        var before = WallLocationStart();

        // Act
        var committed = Transactions.RunInTransaction(Doc, "Move wall",
            doc => _transform!.MoveElement(doc, _wallId!, new Vec3(1, 2, 0)));

        // Assert
        var delta = WallLocationStart() - before;
        using (Assert.Multiple())
        {
            await Assert.That(committed).IsTrue();
            await Assert.That(delta.DistanceTo(new XYZ(1, 2, 0))).IsEqualTo(0.0).Within(Tolerance);
        }
    }

    [Test]
    public async Task MoveElement_ZeroOffset_LeavesWallLocationUnchanged()
    {
        // Arrange
        var before = WallLocationStart();

        // Act
        var committed = Transactions.RunInTransaction(Doc, "No-op move",
            doc => _transform!.MoveElement(doc, _wallId!, Vec3.Zero));

        // Assert — guard VectorUtils.IsZero не должен трогать документ
        var after = WallLocationStart();
        using (Assert.Multiple())
        {
            await Assert.That(committed).IsTrue();
            await Assert.That(after.DistanceTo(before)).IsEqualTo(0.0).Within(Tolerance);
        }
    }

    private XYZ WallLocationStart()
    {
        var wall = Doc.GetElement(_wallId);
        return ((LocationCurve)wall.Location).Curve.GetEndPoint(0);
    }
}
