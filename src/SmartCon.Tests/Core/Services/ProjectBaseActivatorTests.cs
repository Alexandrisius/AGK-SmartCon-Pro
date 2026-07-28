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
    public async Task ActivateForDocumentAsync_EmptyPath_NoConnections_ReturnsNull()
    {
        // #174: empty path (unsaved document) no longer short-circuits before
        // the connections check — it behaves as "no project base matched".
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns([]);
        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);

        var result = await activator.ActivateForDocumentAsync("");

        Assert.Null(result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_EmptyPath_ActiveProject_FallsBackToGeneral()
    {
        // #174: unsaved document while a project base is active — fall back
        // to the first general base.
        var binding = MakeBinding();
        var projectConn = MakeConnection("project-1", BaseType.Project, binding);
        var generalConn = MakeConnection("general-1", BaseType.General);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn, generalConn });
        dbMock.Setup(m => m.GetActiveConnection()).Returns(projectConn);
        dbMock.Setup(m => m.SwitchDatabaseAsync(generalConn.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("");

        Assert.Equal(generalConn.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(generalConn.Id, It.IsAny<CancellationToken>()), Times.Once);
        evalMock.Verify(e => e.Evaluate(It.IsAny<ProjectBaseBinding>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_EmptyPath_ActiveGeneral_KeepsManualSelection()
    {
        // #174: unsaved document while a general base is active — preserve it
        // (do not steal activation with the FIRST general base).
        var projectConn = MakeConnection("project-1", BaseType.Project, MakeBinding());
        var generalFirst = MakeConnection("general-1", BaseType.General);
        var generalCurrent = MakeConnection("general-2", BaseType.General);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn, generalFirst, generalCurrent });
        dbMock.Setup(m => m.GetActiveConnection()).Returns(generalCurrent);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("");

        Assert.Equal(generalCurrent.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_EmptyPath_NoActive_FallsBackToFirstGeneral()
    {
        // #174: unsaved document with nothing active — pick the first general
        // base (same rule as "no project base matched").
        var projectConn = MakeConnection("project-1", BaseType.Project, MakeBinding());
        var generalConn = MakeConnection("general-1", BaseType.General);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn, generalConn });
        dbMock.Setup(m => m.GetActiveConnection()).Returns((DatabaseConnection?)null);
        dbMock.Setup(m => m.SwitchDatabaseAsync(generalConn.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("");

        Assert.Equal(generalConn.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(generalConn.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_EmptyPath_NoGeneralBase_ReturnsNullNoSwitch()
    {
        // #174: unsaved document, only project bases exist — nothing can be
        // done; leave the active base untouched.
        var projectConn = MakeConnection("project-1", BaseType.Project, MakeBinding());
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn });
        dbMock.Setup(m => m.GetActiveConnection()).Returns(projectConn);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("");

        Assert.Null(result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

    [Fact]
    public async Task ActivateForDocumentAsync_NoMatch_ActiveGeneral_KeepsManualSelection()
    {
        var binding = MakeBinding();
        var projectConn = MakeConnection("project-1", BaseType.Project, binding);
        var generalFirst = MakeConnection("general-1", BaseType.General);
        var generalCurrent = MakeConnection("general-2", BaseType.General);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn, generalFirst, generalCurrent });
        dbMock.Setup(m => m.GetActiveConnection()).Returns(generalCurrent);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        evalMock.Setup(e => e.Evaluate(binding, "OTHER-S1.rvt")).Returns(new ProjectBaseMatch(ProjectBaseMatchKind.Mismatch));

        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("OTHER-S1.rvt");

        Assert.Equal(generalCurrent.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActivateForDocumentAsync_NoMatch_ActiveProjectMismatch_FallsBackToFirstGeneral()
    {
        var binding = MakeBinding();
        var projectConn = MakeConnection("project-1", BaseType.Project, binding);
        var generalFirst = MakeConnection("general-1", BaseType.General);
        var generalSecond = MakeConnection("general-2", BaseType.General);
        var dbMock = new Mock<IDatabaseManager>();
        dbMock.Setup(m => m.ListConnections()).Returns(new List<DatabaseConnection> { projectConn, generalFirst, generalSecond });
        dbMock.Setup(m => m.GetActiveConnection()).Returns(projectConn);
        dbMock.Setup(m => m.SwitchDatabaseAsync(generalFirst.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var evalMock = new Mock<IProjectBaseBindingEvaluator>();
        evalMock.Setup(e => e.Evaluate(binding, "OTHER-S1.rvt")).Returns(new ProjectBaseMatch(ProjectBaseMatchKind.Mismatch));

        var activator = new ProjectBaseActivator(dbMock.Object, evalMock.Object);
        var result = await activator.ActivateForDocumentAsync("OTHER-S1.rvt");

        Assert.Equal(generalFirst.Id, result);
        dbMock.Verify(m => m.SwitchDatabaseAsync(generalFirst.Id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
