using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Simple in-memory implementation of <see cref="IFittingMappingRepository"/> for
/// unit tests. Replaces the previous <c>JsonFittingMappingRepository</c> test fixture
/// (removed in ADR-012) without pulling in Revit ExtensibleStorage dependencies.
/// </summary>
internal sealed class InMemoryFittingMappingRepository : IFittingMappingRepository
{
    private List<ConnectorTypeDefinition> _types = [];
    private List<FittingMappingRule> _rules = [];

    public IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes() => _types;

    public IReadOnlyList<FittingMappingRule> GetMappingRules() => _rules;

    public void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types)
    {
        _types = [.. types];
    }

    public void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules)
    {
        _rules = [.. rules];
    }

    public string GetStoragePath() => "InMemory";
}
