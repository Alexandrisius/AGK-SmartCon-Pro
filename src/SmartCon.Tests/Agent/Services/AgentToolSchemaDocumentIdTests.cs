using System.IO;
using System.Reflection;
using System.Text.Json;
using Moq;
using SmartCon.Agent;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Storage;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Agent.Storage;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Regression guard for multi-model routing (#466 case 9 v2): a schema that
/// accepts an optional <c>documentId</c> must declare it INSIDE
/// <c>properties</c>. When it sat at the schema root next to
/// <c>properties</c> (root had <c>additionalProperties:false</c>), the
/// validator reported '$.documentId: unknown property' and
/// <see cref="AgentToolInputRepair"/> deleted the argument, so
/// background-document reads silently ran against the ACTIVE document.
/// </summary>
public sealed class AgentToolSchemaDocumentIdTests
{
    /// <summary>
    /// The closed keyword subset the schema validator/repair understand at the
    /// root (anyOf/definitions are dereference/multi-type keywords handled by
    /// AgentJsonSchema). Anything else at the root is a typo waiting to strip
    /// arguments the same way documentId was stripped.
    /// </summary>
    private static readonly HashSet<string> AllowedRootKeys = new(
        new[] { "type", "properties", "required", "additionalProperties", "$schema", "anyOf", "definitions" });

    /// <summary>
    /// Agent-side production tools only: the Revit-side tools take
    /// IRevitContext/IJobScheduler-shaped dependencies whose interface maps
    /// need RevitAPI metadata, and this host references RevitAPI compile-time
    /// only. The full production catalog (Revit tools included) is guarded by
    /// ProductionAgentParityTests inside a real Revit process.
    /// </summary>
    private static IEnumerable<IAgentTool> ProductionTools() =>
        AgentCoreRegistration.ToolTypes
            .Select(Instantiate)
            .Cast<IAgentTool>()
            .ToList();

    /// <summary>
    /// Instantiates a production tool with inert dependencies: reading
    /// <see cref="IAgentTool.Definition"/> never invokes them. Fails loudly on
    /// new dependency shapes so this guard cannot silently skip a tool.
    /// </summary>
    internal static object Instantiate(Type type)
    {
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .First();
        var arguments = ctor.GetParameters()
            .Select(parameter => ResolveDependency(parameter.ParameterType))
            .ToArray();
        return ctor.Invoke(arguments);
    }

    private static object? ResolveDependency(Type type)
    {
        if (type == typeof(AgentSessionContext))
            return new AgentSessionContext();
        if (type == typeof(AgentDatabase))
            return new AgentDatabase(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "smartcon-schema-guard-unused.db"),
                Array.Empty<AgentSchemaMigration>());
        if (type == typeof(JobReportBuilder))
            return new JobReportBuilder(Mock.Of<IJobStore>(), Array.Empty<IJobAction>());
        if (type == typeof(AgentDecisionsSettings))
            return new AgentDecisionsSettings();
        if (type == typeof(IAgentAwaitableEvent))
            return new StubAwaitableEvent();
        if (type.IsInterface)
            return CreateMock(type);
        if (type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(Func<>))
        {
            return CreateNullFactory(type.GetGenericArguments()[0]);
        }

        if (type.IsValueType)
            return Activator.CreateInstance(type);

        // Optional dependencies (auditLog, sessionContext) tolerate null; a
        // mandatory concrete dependency without a parameterless ctor fails here
        // on purpose: add it to the known list above.
        return Activator.CreateInstance(type);
    }

    private static object CreateMock(Type interfaceType)
    {
        // Mock.Of<T>() via reflection: reading the Mock<T>.Object property by
        // name is ambiguous (re-declared in the closed generic).
        var method = typeof(Mock).GetMethods()
            .Single(candidate => candidate.Name == "Of"
                                 && candidate.IsGenericMethod
                                 && candidate.GetGenericArguments().Length == 1
                                 && candidate.GetParameters().Length == 0);
        return method.MakeGenericMethod(interfaceType).Invoke(null, null)!;
    }

    private static object CreateNullFactory(Type resultType)
    {
        var method = typeof(AgentToolSchemaDocumentIdTests)
            .GetMethod(nameof(NullResult), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType);
        return Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(resultType), method);
    }

    private static T? NullResult<T>() => default;

    [Fact]
    public void DocumentId_IsDeclaredInsideProperties_InEveryProductionTool()
    {
        var offenders = new List<string>();
        foreach (var tool in ProductionTools())
        {
            var schema = tool.Definition.InputSchemaJson;
            if (string.IsNullOrWhiteSpace(schema)
                || !schema.Contains("documentId", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(schema);
            var declaresProperty = document.RootElement.TryGetProperty("properties", out var properties)
                                   && properties.ValueKind == JsonValueKind.Object
                                   && properties.TryGetProperty("documentId", out _);
            if (!declaresProperty)
            {
                offenders.Add(
                    tool.Definition.Name + ": schema mentions documentId but does not " +
                    "declare it in properties");
            }
        }

        Assert.True(
            offenders.Count == 0,
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ValidatorAndRepair_DoNotStripDocumentIdArgument()
    {
        var checkedTools = 0;
        var offenders = new List<string>();
        foreach (var tool in ProductionTools())
        {
            var schema = tool.Definition.InputSchemaJson;
            if (string.IsNullOrWhiteSpace(schema))
                continue;

            using var document = JsonDocument.Parse(schema);
            if (!document.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !properties.TryGetProperty("documentId", out _))
            {
                continue;
            }

            checkedTools++;
            using var arguments = JsonDocument.Parse("{\"documentId\":\"abc\"}");
            var repaired = AgentToolInputRepair.Repair(schema, arguments.RootElement);
            var kept = repaired.Arguments.ValueKind == JsonValueKind.Object
                       && repaired.Arguments.TryGetProperty("documentId", out var value)
                       && value.GetString() == "abc";
            if (!kept)
            {
                offenders.Add(
                    tool.Definition.Name + ": repair removed documentId " +
                    "(repairs: " + string.Join("; ", repaired.Repairs) + ")");
                continue;
            }

            var errors = AgentToolSchemaValidator.Validate(schema, "{\"documentId\":\"abc\"}");
            if (errors.Any(error => error.Contains(
                    "$.documentId", StringComparison.Ordinal)))
            {
                offenders.Add(
                    tool.Definition.Name + ": validator flags documentId: " +
                    string.Join("; ", errors));
            }
        }

        // The agent-side catalog currently declares no documentId parameter;
        // the "at least one tool exercises the documentId path" staleness
        // guard lives in ProductionAgentParityTests, which sees the FULL
        // production catalog (Revit tools included) inside a real Revit.
        _ = checkedTools;
        Assert.True(
            offenders.Count == 0,
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoToolSchema_HasUnexpectedRootKeys()
    {
        var offenders = new List<string>();
        foreach (var tool in ProductionTools())
        {
            var schema = tool.Definition.InputSchemaJson;
            if (string.IsNullOrWhiteSpace(schema))
                continue;

            using var document = JsonDocument.Parse(schema);
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (!AllowedRootKeys.Contains(member.Name))
                {
                    offenders.Add(
                        tool.Definition.Name + ": unexpected root key '" + member.Name + "'");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            string.Join(Environment.NewLine, offenders));
    }

    private sealed class StubAwaitableEvent : IAgentAwaitableEvent
    {
        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(funcWithApp(this));
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actionWithApp(this);
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }
}
