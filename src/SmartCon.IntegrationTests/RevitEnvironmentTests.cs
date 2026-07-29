using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;

namespace SmartCon.IntegrationTests;

/// <summary>
/// Smoke-тесты окружения: инъекция в реальный процесс Revit и выполнение
/// на его API-потоке. Падают первыми, если окружение сконфигурировано неверно.
/// </summary>
public sealed class RevitEnvironmentTests : RevitApiTest
{
    [Test]
    public async Task Application_Injected_ReportsInstalledRevit()
    {
        // Act
        var version = Application.VersionNumber;
        var build = Application.VersionBuild;

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(version).IsNotEmpty();
            await Assert.That(build).IsNotEmpty();
        }
    }

    [Test]
    public async Task NewProjectDocument_InMemory_CreatesClosableDocument()
    {
        // Act
        var document = Application.NewProjectDocument(UnitSystem.Metric);

        // Assert
        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(document.IsValidObject).IsTrue();
                await Assert.That(document.IsFamilyDocument).IsFalse();
            }
        }
        finally
        {
            document.Close(false);
        }
    }
}
