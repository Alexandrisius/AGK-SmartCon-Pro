using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.FamilyManager.Events.Fakes;

/// <summary>
/// No-op <see cref="IRevitContextWriter"/> for unit tests. Captures the
/// last context object so tests can assert that
/// <see cref="FamilyManager.Events.FamilyManagerAwaitableEvent.ProcessQueue(object)"/>
/// invoked it as expected.
/// </summary>
internal sealed class FakeRevitContextWriter : IRevitContextWriter
{
    public int CallCount { get; private set; }
    public object? LastContext { get; private set; }

    public void SetContext(object revitUIApplication)
    {
        CallCount++;
        LastContext = revitUIApplication;
    }
}
