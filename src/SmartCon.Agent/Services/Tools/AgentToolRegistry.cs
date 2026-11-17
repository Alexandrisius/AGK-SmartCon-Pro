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

            _byName[name] = tool;
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