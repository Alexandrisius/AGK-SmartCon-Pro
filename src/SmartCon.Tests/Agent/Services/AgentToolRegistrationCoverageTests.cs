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
            if (type.IsAbstract || type.IsGenericTypeDefinition || !type.IsPublic)
                continue;
            if (toolInterface.IsAssignableFrom(type))
                yield return type;
        }
    }

    /// <summary>
    /// The unit host has no RevitAPI runtime (ExcludeAssets=runtime), so types
    /// deriving from Revit API bases cannot load here. Agent tools never do:
    /// their inheritance chain is Core-only, so every IAgentTool implementer
    /// is among the loadable types and the guard still sees all of them (the
    /// integration parity test additionally resolves the full catalog inside
    /// a real Revit process).
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

    [Fact]
    public void ToolTypes_HaveNoDuplicates()
    {
        var all = AgentToolRegistration.ToolTypes.Concat(AgentCoreRegistration.ToolTypes).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
