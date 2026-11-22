using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent;

namespace SmartCon.IntegrationTests.Agent.Support;

/// <summary>
/// Headless double of the production project context provider. The production
/// <see cref="AgentProjectContextProvider"/> casts the event callback object to
/// <c>UIApplication</c>; JIT-loading RevitAPIUI inside the UI-less TUnit host
/// throws FileLoadException (0x8007045A) and leaves the Revit session crashing
/// with a native access violation at exit — the documented RevitAPIUI trap.
/// The double feeds the SAME real document through the DB-level
/// <c>BuildDocument</c> path the production provider uses after the cast.
/// </summary>
internal sealed class HeadlessProjectContextProvider : IAgentProjectContextProvider
{
    private readonly IRevitContext _context;

    public HeadlessProjectContextProvider(IRevitContext context) => _context = context;

    public Task<AgentProjectContext> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            AgentProjectContextProvider.BuildDocument(_context.GetDocument(), ct));
    }
}
