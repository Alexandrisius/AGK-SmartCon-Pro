using Moq;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class ProjectBaseActivatorTests
{
    private static DatabaseConnection MakeConnection(string id, BaseType kind, ProjectBaseBinding? binding = null)
    {
        return new DatabaseConnection(
            id,
            $"DB_{id}",
            $"C:\\Db_{id}",
            DateTimeOffset.UtcNow,
            Kind: kind,
            ProjectBinding: binding);
    }

    private static ProjectBaseBinding MakeBinding()
    {
        var template = new FileNameTemplate
        {
            Blocks =
            [
                new() { Index = 0, Field = "project", ParseRule = new ParseRule { Mode = ParseMode.DelimiterSegment, Delimiter = "-", SegmentIndex = 1 } }
            ]
        };
        return new ProjectBaseBinding(template, []);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_EmptyPath_ReturnsNull()
    {
        var dbMock = new Mock<IDatabaseManager>();
        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);

        var result = await activator.ActivateForDocumentAsync("");

        Assert.Null(result);
        dbMock.Verify(m => m.ListConnections(), Times.Never);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_NoConnections_ReturnsNull()
    {
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns([]);
        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);

        var result = await activator.ActivateForDocumentAsync("PRJ-S1.rvt");

        Assert.Null(result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_MatchingProjectBase_SwitchesAndReturnsId()
    {
        var binding = MakeBinding();
        var projectConn = MakeConnection("project-1", BaseType.Project, binding);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn });
        dbMock.Setup(m => m.GetActiveConnection()).Returns((DatabaseConnection?)null);
        dbMock.Setup(m => m.SwitchDatabaseAsync(projectConn.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        evalMock.Setup(e => e.Evaluate(binding, "PRJ-S1.rvt")).Returns(new ProjectBaseMatch(ProjectBaseMatchKind.Match));

        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("PRJ-S1.rvt");

        Assert.Equal(projectConn.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(projectConn.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_NoMatchingProjectBase_FallsBackToGeneral()
    {
        var binding = MakeBinding();
        var projectConn = MakeConnection("project-1", BaseType.Project, binding);
        var generalConn = MakeConnection("general-1", BaseType.General);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn, generalConn });
        dbMock.Setup(m => m.GetActiveConnection()).Returns((DatabaseConnection?)null);
        dbMock.Setup(m => m.SwitchDatabaseAsync(generalConn.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        evalMock.Setup(e => e.Evaluate(binding, "OTHER-S1.rvt")).Returns(new ProjectBaseMatch(ProjectBaseMatchKind.Mismatch));

        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("OTHER-S1.rvt");

        Assert.Equal(generalConn.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(generalConn.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_AlreadyActiveMatchingProjectBase_DoesNotSwitch()
    {
        var binding = MakeBinding();
        var projectConn = MakeConnection("project-1", BaseType.Project, binding);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn });
        dbMock.Setup(m => m.GetActiveConnection()).Returns(projectConn);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        evalMock.Setup(e => e.Evaluate(binding, "PRJ-S1.rvt")).Returns(new ProjectBaseMatch(ProjectBaseMatchKind.Match));

        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("PRJ-S1.rvt");

        Assert.Equal(projectConn.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_OnlyProjectBasesAndNoMatch_ReturnsNull()
    {
        var binding = MakeBinding();
        var projectConn = MakeConnection("project-1", BaseType.Project, binding);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn });
        dbMock.Setup(m => m.GetActiveConnection()).Returns((DatabaseConnection?)null);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        evalMock.Setup(e => e.Evaluate(binding, "OTHER-S1.rvt")).Returns(new ProjectBaseMatch(ProjectBaseMatchKind.Mismatch));

        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("OTHER-S1.rvt");

        Assert.Null(result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
