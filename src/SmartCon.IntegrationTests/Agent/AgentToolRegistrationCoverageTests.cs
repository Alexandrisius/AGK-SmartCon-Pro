using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent;
using SmartCon.Agent.Services;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.Revit.Agent;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Registry-side half of the registration-coverage guard, inside the real Revit
/// host: the tool registry the production agent gets must be exactly the set
/// the shared registrations declare. The assembly scan for tools that are
/// implemented but never declared lives in the unit project
/// (SmartCon.Tests.AgentToolRegistrationCoverageTests) on purpose — an
/// assembly-wide GetTypes() of SmartCon.Revit loads types derived from Revit
/// API UI bases and crashes the headless test host.
/// </summary>
public sealed class AgentToolRegistrationCoverageTests : RevitApiTest
{
    private Document? _document;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Close()
    {
        _document?.Close(false);
    }

    /// <summary>
    /// A declared type whose registration line was deleted, a registration
    /// missing from the declarations, and a type registered twice are all
    /// caught here.
    /// </summary>
    [Test]
    public async Task ProductionRegistry_IsExactlyTheDeclaredToolSet()
    {
        using var host = ProductionAgentHost.Create(Doc);
        var registryTypes = host.Registry.GetAll()
            .Select(tool => tool.GetType())
            .ToList();
        var declared = AgentToolRegistration.ToolTypes
            .Concat(AgentCoreRegistration.ToolTypes)
            .ToList();

        var notRegistered = declared
            .Where(type => !registryTypes.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var undeclared = registryTypes
            .Where(type => !declared.Contains(type))
            .Select(type => type.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var registeredTwice = registryTypes
            .GroupBy(type => type)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.Name + " x" + group.Count())
            .ToList();

        await Assert.That(string.Join(", ", notRegistered)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", undeclared)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", registeredTwice)).IsEqualTo(string.Empty);
    }
}
