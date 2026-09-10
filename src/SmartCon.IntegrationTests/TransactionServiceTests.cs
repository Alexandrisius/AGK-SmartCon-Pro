using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests;

/// <summary>
/// Граница «SmartCon ↔ Revit API»: RevitTransactionService (I-03) против
/// настоящих транзакций Revit в реальном документе.
/// </summary>
public sealed class TransactionServiceTests : RevitApiTest
{
    // Поля без инициализаторов: инициализация полей экземпляра выполняется до
    // инъекции Revit (TUnit 1.33+), преждевременная загрузка RevitAPI ломает
    // инжектор (Nice3point/RevitUnit#78). Значения назначаются в [Before(Test)].
    private Document? _document;
    private RevitTransactionService? _transactions;

    private Document Doc => _document!;
    private RevitTransactionService Transactions => _transactions!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void OpenDocument()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasWallType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет ни одного WallType — сидирование стены невозможно");
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task RunInTransaction_CommittedAction_PersistsCreatedWall()
    {
        // Act
        var committed = Transactions.RunInTransaction(Doc, "Seed wall", doc =>
        {
            var level = ModelSeed.CreateLevel(doc);
            _ = ModelSeed.TryCreateWall(doc, level.Id)
                ?? throw new InvalidOperationException("WallType недоступен после проверки HasWallType");
        });

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(committed).IsTrue();
            await Assert.That(ModelSeed.CountInstances<Wall>(Doc)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task RunInTransaction_ThrowingAction_RollsBackAndRethrows()
    {
        // Assert — исключение пробрасывается, модель остаётся нетронутой
        using (Assert.Multiple())
        {
            await Assert.That(() => Transactions.RunInTransaction(Doc, "Failing", doc =>
            {
                var level = ModelSeed.CreateLevel(doc);
                _ = ModelSeed.TryCreateWall(doc, level.Id);
                throw new InvalidOperationException("Simulated failure after model change");
            })).Throws<InvalidOperationException>();
            await Assert.That(ModelSeed.CountInstances<Wall>(Doc)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task RunAndRollback_ExecutedAction_LeavesDocumentUntouched()
    {
        // Act — используется overload через IRevitContext (покрывает и его)
        var executed = Transactions.RunAndRollback("Probe", doc =>
        {
            var level = ModelSeed.CreateLevel(doc);
            _ = ModelSeed.TryCreateWall(doc, level.Id);
        });

        // Assert — действие выполнилось, но ничего не закоммичено
        using (Assert.Multiple())
        {
            await Assert.That(executed).IsTrue();
            await Assert.That(ModelSeed.CountInstances<Wall>(Doc)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task RunInTransaction_SilentRollback_NeverReportsSuccess()
    {
        // Issue #178: удаление последнего типа системной семьи Revit отклоняет
        // двумя способами (машино-зависимо): ArgumentException upfront или
        // error-failure на коммите с молчаливым RolledBack. Контракт #178:
        // операция НИКОГДА не должна отчитаться успехом — либо исключение,
        // либо false; тип при этом остаётся живым.
        var singleTypeFamily = new FilteredElementCollector(Doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .GroupBy(t => t.FamilyName)
            .FirstOrDefault(g => g.Count() == 1);
        if (singleTypeFamily is null)
        {
            Skip.Test("В шаблоне нет семьи стен с единственным типом — сидирование невозможно");
        }

        var victim = singleTypeFamily!.First();

        var threw = false;
        var committed = true;
        try
        {
            committed = Transactions.RunInTransaction(Doc, "Delete last family type", doc =>
            {
                doc.Delete(victim.Id);
            });
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            threw = true;
        }

        using (Assert.Multiple())
        {
            await Assert.That(threw || !committed).IsTrue();
            await Assert.That(Doc.GetElement(victim.Id)).IsNotNull();
        }
    }
}
