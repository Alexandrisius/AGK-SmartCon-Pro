using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Wrappers;

namespace SmartCon.IntegrationTests.PipeConnect;

/// <summary>
/// Граница «SmartCon ↔ Revit API»: ConnectorWrapper.ToProxy (I-05) против
/// настоящих MEP-коннекторов реальной трубы — ядро доменной модели PipeConnect.
/// </summary>
public sealed class ConnectorWrapperTests : PipeModelFixture
{
    private const double Tolerance = 1e-6;

    [Test]
    public async Task ToProxy_FreePipeConnector_CapturesRealGeometryAndState()
    {
        // Arrange
        var connector = ModelSeed.FindConnectorAt(Pipe(FirstPipeId), P0);

        // Act
        var proxy = ConnectorWrapper.ToProxy(connector);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(proxy.OwnerElementId).IsEqualTo(FirstPipeId);
            await Assert.That(proxy.Origin.DistanceTo(connector.CoordinateSystem.Origin)).IsEqualTo(0.0).Within(Tolerance);
            await Assert.That(proxy.BasisZ.GetLength()).IsEqualTo(1.0).Within(Tolerance);
            await Assert.That(proxy.Radius).IsGreaterThan(0.0);
            await Assert.That(proxy.Domain).IsEqualTo(Domain.DomainPiping);
            await Assert.That(proxy.IsFree).IsTrue();
        }
    }

    [Test]
    public async Task ToProxy_AfterConnectTo_ReportsConnectorAsConnected()
    {
        // Act
        ConnectPipesAt(FirstPipeId, SecondPipeId, P1);

        // Assert
        var proxy = ConnectorWrapper.ToProxy(ModelSeed.FindConnectorAt(Pipe(FirstPipeId), P1));
        await Assert.That(proxy.IsFree).IsFalse();
    }

    [Test]
    public async Task DescribeConnector_RealPipeConnector_ContainsDiagnostics()
    {
        // Arrange
        var connector = ModelSeed.FindConnectorAt(Pipe(FirstPipeId), P0);

        // Act
        var description = ConnectorWrapper.DescribeConnector(connector);

        // Assert — диагностика для логов: никогда не бросает, несёт контекст владельца
        using (Assert.Multiple())
        {
            await Assert.That(description).Contains($"elementId={FirstPipeId.GetValue()}");
            await Assert.That(description).Contains("domain=");
            await Assert.That(description).Contains("isConnected=False");
        }
    }
}
