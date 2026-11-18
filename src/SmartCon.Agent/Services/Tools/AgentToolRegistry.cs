using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed class AgentToolRegistry : IToolRegistry
{
    private readonly List<IAgentTool> _tools;
    private readonly Dictionary<string, IAgentTool> _byName;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        if (tools is null) throw new ArgumentNullException(nameof(tools));

        _tools = tools.ToList();
        _byName = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in _tools)
        {
            var name = tool.Definition.Name;
            if (_byName.ContainsKey(name))
                throw new InvalidOperationException("Duplicate agent tool name: " + name);

            ValidateDefinition(tool.Definition);
            _byName[name] = tool;
        }
    }

    /// <summary>
    /// The LLM payload builder parses every InputSchemaJson on every request; an
    /// invalid schema would break the whole turn (BLOCKER found in review). Fail at
    /// startup instead, naming the tool.
    /// </summary>
    private static void ValidateDefinition(AgentToolDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new InvalidOperationException("Agent tool without a name.");

        if (string.IsNullOrWhiteSpace(definition.Description))
            throw new InvalidOperationException(
                "Agent tool '" + definition.Name + "' has no description.");

        try
        {
            using var document = JsonDocument.Parse(definition.InputSchemaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Agent tool '" + definition.Name + "' schema must be a JSON object.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Agent tool '" + definition.Name + "' has an invalid InputSchemaJson: " +
                ex.Message, ex);
        }
    }

    public IReadOnlyList<IAgentTool> GetAll() => _tools;

    public bool TryGet(string name, out IAgentTool? tool)
    {
        if (_byName.TryGetValue(name, out var found))
        {
            tool = found;
            return true;
        }

        tool = null;
        return false;
    }
}