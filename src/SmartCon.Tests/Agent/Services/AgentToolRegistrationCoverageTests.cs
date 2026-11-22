using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Agent;
using SmartCon.Agent.Services;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Registration-coverage guard: every concrete agent tool that ships in the
/// production assemblies must be registered by the shared production
/// registration (<see cref="AgentToolRegistration"/> or
/// <see cref="AgentCoreRegistration"/>). This is what keeps the ~50 planned
/// tools from being implemented but silently missing from the DI catalog.
/// </summary>
public class AgentToolRegistrationCoverageTests
{
    /// <summary>
    /// Deliberate exclusions with reasons. Keep this list empty unless the type
    /// is genuinely not part of the production catalog.
    /// </summary>
    private static readonly HashSet<Type> Exclusions = new();

    private static IEnumerable<Type> ImplementationsOf(Type toolInterface, params Assembly[] assemblies)
    {
        foreach (var type in assemblies.SelectMany(GetLoadableTypes))
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition)
                continue;
            if (!(type.IsPublic || type.IsNestedPublic))
                continue;
            if (toolInterface.IsAssignableFrom(type))
                yield return type;
        }
    }

    /// <summary>
    /// The unit host has no RevitAPI runtime (ExcludeAssets=runtime), so types
    /// deriving from Revit API bases cannot load here. Agent tools never do:
    /// their inheritance chain is Core-only, so every IAgentTool implementer
    /// is among the loadable types and the guard still sees all of them. This
    /// scan is the reason SmartCon.Tests may reference SmartCon.Revit (metadata
    /// only, dependency rule): the same GetTypes() inside the Revit test host
    /// loads Revit-API-UI-derived types and crashes it, so it cannot live in
    /// the integration project. The registry itself is checked there
    /// (AgentToolRegistrationCoverageTests in SmartCon.IntegrationTests).
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }

    private static HashSet<Type> RegisteredToolTypes() => new(
        AgentToolRegistration.ToolTypes.Concat(AgentCoreRegistration.ToolTypes));

    [Fact]
    public void EveryPublicAgentTool_IsRegisteredInProduction()
    {
        var registered = RegisteredToolTypes();

        var missing = ImplementationsOf(
                typeof(IAgentTool),
                typeof(AgentToolRegistration).Assembly,
                typeof(AgentOrchestrator).Assembly)
            .Where(type => !registered.Contains(type) && !Exclusions.Contains(type))
            .ToList();

        Assert.True(
            missing.Count == 0,
            string.Join(Environment.NewLine, missing.Select(type =>
                "IAgentTool '" + type.Name + "' is implemented but not registered in production " +
                "(AgentToolRegistration/AgentCoreRegistration)")));
    }

    [Fact]
    public void ToolTypes_ContainOnlyRealAgentTools()
    {
        var impostors = RegisteredToolTypes()
            .Where(type => !typeof(IAgentTool).IsAssignableFrom(type))
            .ToList();

        Assert.True(
            impostors.Count == 0,
            string.Join(Environment.NewLine, impostors.Select(type =>
                "Type '" + type.Name + "' is listed in ToolTypes but does not implement IAgentTool")));
    }

    [Fact]
    public void RevitTypedRegistrations_AreAllCoveredByToolTypes()
    {
        var services = new ServiceCollection();
        services.AddRevitAgentTools();

        var registered = RegisteredToolTypes();
        var uncovered = services
            .Where(descriptor => descriptor.ServiceType == typeof(IAgentTool)
                                 && descriptor.ImplementationType is not null)
            .Select(descriptor => descriptor.ImplementationType!)
            .Where(type => !registered.Contains(type))
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            string.Join(Environment.NewLine, uncovered.Select(type =>
                "IAgentTool '" + type.Name + "' is registered by AddRevitAgentTools but missing from ToolTypes")));
    }

    // The inverse — a declared type whose registration line was deleted — needs
    // the factory registrations resolved, i.e. the real graph: it is guarded by
    // AgentToolRegistrationCoverageTests in SmartCon.IntegrationTests.

    [Fact]
    public void ToolTypes_HaveNoDuplicates()
    {
        var all = AgentToolRegistration.ToolTypes.Concat(AgentCoreRegistration.ToolTypes).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
