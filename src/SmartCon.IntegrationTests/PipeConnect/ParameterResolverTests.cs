using Autodesk.Revit.DB;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Parameters;

namespace SmartCon.IntegrationTests.PipeConnect;

/// <summary>
/// RevitParameterResolver (MEPCurve-ветка): резолв и запись диаметра трубы
/// через RBS_PIPE_DIAMETER_PARAM на реальном элементе.
/// </summary>
public sealed class ParameterResolverTests : PipeModelFixture
{
    private const double Tolerance = 1e-6;

    private RevitParameterResolver? _resolver;
    private RevitParameterResolver Resolver => _resolver!;

    [Before(Test)]
    public void CreateResolver()
    {
        _resolver = new RevitParameterResolver(new FamilyFormulaCache());
    }

    [Test]
    public async Task GetConnectorRadiusDependencies_Pipe_ReturnsDirectDiameterWrite()
    {
        // Act
        var dependencies = Resolver.GetConnectorRadiusDependencies(Doc, FirstPipeId, connectorIndex: 0);

        // Assert — для труб всегда прямая запись в instance-параметр диаметра
        using (Assert.Multiple())
        {
            await Assert.That(dependencies.Count).IsEqualTo(1);
            await Assert.That(dependencies[0].BuiltIn).IsEqualTo(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            await Assert.That(dependencies[0].IsInstance).IsTrue();
            await Assert.That(dependencies[0].Formula).IsNull();
        }
    }

    [Test]
    public async Task TrySetConnectorRadius_Pipe_UpdatesDiameterParameter()
    {
        // Arrange
        var pipe = Pipe(FirstPipeId);
        var diameterParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        var originalDiameter = diameterParam.AsDouble();
        var targetRadius = originalDiameter * 0.75;

        // Act — запись параметра только внутри транзакции (I-03)
        var succeeded = false;
        Transactions.RunInTransaction(Doc, "Set radius", doc =>
        {
            succeeded = Resolver.TrySetConnectorRadius(doc, FirstPipeId, 0, targetRadius);
            doc.Regenerate();
        });

        // Assert — параметр хранит ДИАМЕТР (радиус × 2)
        var actualDiameter = Pipe(FirstPipeId).get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).AsDouble();
        using (Assert.Multiple())
        {
            await Assert.That(succeeded).IsTrue();
            await Assert.That(actualDiameter).IsEqualTo(targetRadius * 2.0).Within(Tolerance);
        }
    }
}
