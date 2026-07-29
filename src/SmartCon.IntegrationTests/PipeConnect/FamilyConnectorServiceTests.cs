using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Family;
using SmartCon.Revit.Wrappers;

namespace SmartCon.IntegrationTests.PipeConnect;

/// <summary>
/// RevitFamilyConnectorService: запись CTC (ConnectionTypeCode, ADR-002) в
/// описание типоразмера трубы + полный цикл чтения кода через ConnectorWrapper —
/// тот путь, по которому PipeConnect определяет тип соединения в production.
/// </summary>
public sealed class FamilyConnectorServiceTests : PipeModelFixture
{
    private static readonly ConnectorTypeDefinition Grooved = new()
    {
        Code = 7,
        Name = "Grooved",
        Description = "Канавочный"
    };

    private RevitFamilyConnectorService? _service;
    private RevitFamilyConnectorService Service => _service!;

    [Before(Test)]
    public void CreateService()
    {
        _service = new RevitFamilyConnectorService();
    }

    [Test]
    public async Task SetConnectorTypeCode_Pipe_WritesFormattedDescriptionToPipeType()
    {
        // Act — запись в параметр типа только внутри транзакции (I-03)
        var succeeded = false;
        Transactions.RunInTransaction(Doc, "Set CTC", doc =>
        {
            succeeded = Service.SetConnectorTypeCode(doc, FirstPipeId, 0, Grooved);
        });

        // Assert — формат "КОД.НАЗВАНИЕ.ОПИСАНИЕ" в ALL_MODEL_DESCRIPTION типа
        var pipeType = Doc.GetElement(Pipe(FirstPipeId).GetTypeId());
        var description = pipeType.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString();
        using (Assert.Multiple())
        {
            await Assert.That(succeeded).IsTrue();
            await Assert.That(description).IsEqualTo("7.Grooved.Канавочный");
        }
    }

    [Test]
    public async Task SetConnectorTypeCode_ThenToProxy_ReadsBackFullCtcLoop()
    {
        // Arrange — production-цикл: сервис пишет, ConnectorWrapper читает
        Transactions.RunInTransaction(Doc, "Set CTC", doc =>
        {
            _ = Service.SetConnectorTypeCode(doc, FirstPipeId, 0, Grooved);
        });

        // Act
        var proxy = ConnectorWrapper.ToProxy(ModelSeed.FindConnectorAt(Pipe(FirstPipeId), P0));

        // Assert — ConnectorDescription.Parse разобрал все три сегмента
        using (Assert.Multiple())
        {
            await Assert.That(proxy.ConnectionTypeCode.Value).IsEqualTo(7);
            await Assert.That(proxy.ConnectionName).IsEqualTo("Grooved");
            await Assert.That(proxy.ConnectionDescription).IsEqualTo("Канавочный");
        }
    }

    [Test]
    public async Task SetConnectorTypeCode_Wall_ReturnsFalseAndLeavesTypeUntouched()
    {
        // Arrange
        if (!ModelSeed.HasWallType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет WallType");
        }

        ElementId? wallId = null;
        Transactions.RunInTransaction(Doc, "Seed wall", doc =>
        {
            var level = ModelSeed.CreateLevel(doc);
            wallId = ModelSeed.TryCreateWall(doc, level.Id)?.Id;
        });

        // Act — CTC поддерживается только для MEPCurve/FlexPipe (фитинги — через VirtualCtcStore)
        var succeeded = true;
        Transactions.RunInTransaction(Doc, "Set CTC on wall", doc =>
        {
            succeeded = Service.SetConnectorTypeCode(doc, wallId!, 0, Grooved);
        });

        // Assert
        var wallType = Doc.GetElement(Doc.GetElement(wallId).GetTypeId());
        var description = wallType.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString();
        using (Assert.Multiple())
        {
            await Assert.That(succeeded).IsFalse();
            await Assert.That(description).IsNotEqualTo("7.Grooved.Канавочный");
        }
    }
}
