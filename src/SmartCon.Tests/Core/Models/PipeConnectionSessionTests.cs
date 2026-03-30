using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

/// <summary>
/// PipeConnectionSession содержит ConnectorProxy (Revit-типы XYZ, ElementId, Domain).
/// Тесты требуют Revit runtime.
/// </summary>
[Trait("Category", "RevitRequired")]
public sealed class PipeConnectionSessionTests
{
    [Fact]
    public void NewSession_HasDefaultState()
    {
        var session = new PipeConnectionSession();

        Assert.Equal(PipeConnectState.AwaitingStaticSelection, session.State);
        Assert.Null(session.StaticConnector);
        Assert.Null(session.DynamicConnector);
        Assert.Null(session.DynamicChain);
        Assert.Empty(session.ProposedFittings);
        Assert.Equal(0, session.RotationAngleDeg);
        Assert.False(session.MoveEntireChain);
    }

    [Fact]
    public void Reset_RestoresDefaultState()
    {
        var session = new PipeConnectionSession
        {
            State = PipeConnectState.PostProcessing,
            RotationAngleDeg = 45,
            MoveEntireChain = true
        };

        session.Reset();

        Assert.Equal(PipeConnectState.AwaitingStaticSelection, session.State);
        Assert.Equal(0, session.RotationAngleDeg);
        Assert.False(session.MoveEntireChain);
        Assert.Empty(session.ProposedFittings);
    }
}
