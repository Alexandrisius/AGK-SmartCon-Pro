using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Базовая фикстура для тестов на трубной MEP-модели: in-memory документ
/// с тремя коллинеарными трубами (0-10-20-30 ft), созданными в закоммиченной
/// транзакции. Храним только ElementId (I-05).
///
/// Поля без инициализаторов: инициализация полей экземпляра выполняется до
/// инъекции Revit (TUnit 1.33+), преждевременная загрузка RevitAPI ломает
/// инжектор (Nice3point/RevitUnit#78). Значения назначаются в [Before(Test)].
/// </summary>
public abstract class PipeModelFixture : RevitApiTest
{
    protected const double Elevation = 0.0;

    // Ленивые свойства вместо static readonly — см. remark выше.
    protected static XYZ P0 => new(0, 0, Elevation);
    protected static XYZ P1 => new(10, 0, Elevation);
    protected static XYZ P2 => new(20, 0, Elevation);
    protected static XYZ P3 => new(30, 0, Elevation);

    private Document? _document;
    private RevitTransactionService? _transactions;
    private ElementId? _firstPipeId;
    private ElementId? _secondPipeId;
    private ElementId? _thirdPipeId;

    protected Document Doc => _document!;
    protected RevitTransactionService Transactions => _transactions!;
    protected ElementId FirstPipeId => _firstPipeId!;
    protected ElementId SecondPipeId => _secondPipeId!;
    protected ElementId ThirdPipeId => _thirdPipeId!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void SeedPipeModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasPipeTypes(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет PipeType/PipingSystemType — сидирование труб невозможно");
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));

        Transactions.RunInTransaction(Doc, "Seed pipes", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, Elevation);
            _firstPipeId = CreatePipeChecked(doc, level.Id, P0, P1).Id;
            _secondPipeId = CreatePipeChecked(doc, level.Id, P1, P2).Id;
            _thirdPipeId = CreatePipeChecked(doc, level.Id, P2, P3).Id;
            doc.Regenerate();
        });
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void ClosePipeModel()
    {
        Doc.Close(false);
    }

    protected Pipe Pipe(ElementId id) => (Pipe)Doc.GetElement(id);

    /// <summary>Соединяет две трубы в заданной точке стыка (внутри транзакции).</summary>
    protected void ConnectPipesAt(ElementId firstId, ElementId secondId, XYZ joint)
    {
        Transactions.RunInTransaction(Doc, "Connect pipes", doc =>
        {
            var first = ModelSeed.FindConnectorAt(Pipe(firstId), joint);
            var second = ModelSeed.FindConnectorAt(Pipe(secondId), joint);
            first.ConnectTo(second);
            doc.Regenerate();
        });
    }

    private static Pipe CreatePipeChecked(Document doc, ElementId levelId, XYZ start, XYZ end)
    {
        return ModelSeed.TryCreatePipe(doc, levelId, start, end)
            ?? throw new InvalidOperationException("PipeType недоступен после проверки HasPipeTypes");
    }
}
