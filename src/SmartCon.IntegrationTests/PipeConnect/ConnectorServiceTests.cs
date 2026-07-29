using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Selection;

namespace SmartCon.IntegrationTests.PipeConnect;

/// <summary>
/// ConnectorService (IConnectorService) против реальных коннекторов:
/// connect/disconnect roundtrip и детерминированный порядок свободных
/// коннекторов (issue #163: ConnectorSet перечисляется в случайном порядке).
/// </summary>
public sealed class ConnectorServiceTests : PipeModelFixture
{
    private const double Tolerance = 1e-6;

    private ConnectorService? _connectors;
    private ConnectorService Connectors => _connectors!;

    [Before(Test)]
    public void CreateService()
    {
        _connectors = new ConnectorService();
    }

    [Test]
    public async Task GetAllFreeConnectors_Pipe_ReturnsTwoInDeterministicOrder()
    {
        // Act — ConnectorSet отдаёт коннекторы в случайном порядке (#163),
        // сервис обязан вернуть стабильный геометрический порядок
        var first = Connectors.GetAllFreeConnectors(Doc, FirstPipeId);
        var second = Connectors.GetAllFreeConnectors(Doc, FirstPipeId);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(first.Count).IsEqualTo(2);
            await Assert.That(first[0].ConnectorIndex).IsEqualTo(second[0].ConnectorIndex);
            await Assert.That(first[1].ConnectorIndex).IsEqualTo(second[1].ConnectorIndex);
            await Assert.That(first[0].Origin.X).IsLessThan(first[1].Origin.X);
        }
    }

    [Test]
    public async Task ConnectTo_ThenDisconnectAll_RoundTripsConnectionState()
    {
        // Arrange
        var jointFirst = Connectors.GetAllFreeConnectors(Doc, FirstPipeId)[1];   // стык X=10
        var jointSecond = Connectors.GetAllFreeConnectors(Doc, SecondPipeId)[0]; // стык X=10

        // Act 1 — connect
        var connected = false;
        Transactions.RunInTransaction(Doc, "Connect", doc =>
        {
            connected = Connectors.ConnectTo(Doc, FirstPipeId, jointFirst.ConnectorIndex,
                SecondPipeId, jointSecond.ConnectorIndex);
            doc.Regenerate();
        });

        // Assert 1
        var afterConnect = Connectors.RefreshConnector(Doc, FirstPipeId, jointFirst.ConnectorIndex);
        using (Assert.Multiple())
        {
            await Assert.That(connected).IsTrue();
            await Assert.That(afterConnect!.IsFree).IsFalse();
        }

        // Act 2 — disconnect
        Transactions.RunInTransaction(Doc, "Disconnect", doc =>
        {
            Connectors.DisconnectAllFromConnector(Doc, FirstPipeId, jointFirst.ConnectorIndex);
            doc.Regenerate();
        });

        // Assert 2 — оба конца снова свободны
        var afterDisconnect = Connectors.RefreshConnector(Doc, FirstPipeId, jointFirst.ConnectorIndex);
        await Assert.That(afterDisconnect!.IsFree).IsTrue();
    }

    [Test]
    public async Task GetNearestFreeConnector_PointNearJoint_ReturnsJointConnector()
    {
        // Act — точка клика ближе к стыку X=10, чем к началу X=0
        var nearest = Connectors.GetNearestFreeConnector(Doc, FirstPipeId, new XYZ(9, 0, Elevation));

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(nearest).IsNotNull();
            await Assert.That(nearest!.Origin.X).IsEqualTo(10.0).Within(Tolerance);
        }
    }
}
