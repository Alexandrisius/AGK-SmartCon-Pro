using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Resolves a project parameter to its shared-parameter GUID through the
/// definition element (<c>doc.GetElement(definition.Id)</c> as
/// <see cref="SharedParameterElement"/>). The cache is per document scan: the
/// same definition is met once per parameter slot, so the element lookup is not
/// repeated for every family type or instance.
/// </summary>
internal sealed class AgentSharedParameterResolver
{
    private readonly Document _doc;
    private readonly Dictionary<long, string?> _byDefinitionId = new();
    private readonly Dictionary<string, string> _nameByGuid = new(StringComparer.Ordinal);

    public AgentSharedParameterResolver(Document doc)
    {
        _doc = doc;
    }

    /// <summary>The shared GUID, or null for a family/built-in parameter.</summary>
    public string? ResolveGuid(Parameter parameter)
    {
        try
        {
            // Definition.Id lives on InternalDefinition (the project-context
            // definition of built-in, project and shared parameters alike).
            if (parameter.Definition is not InternalDefinition definition)
                return null;

            var definitionId = definition.Id.GetValue();
            if (_byDefinitionId.TryGetValue(definitionId, out var cached))
                return cached;

            var resolved = _doc.GetElement(definition.Id) is SharedParameterElement shared
                ? shared.GuidValue.ToString("D")
                : null;
            _byDefinitionId.Add(definitionId, resolved);
            return resolved;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The parameter-name prefix used as a source-level FOP bucket: the part up to
    /// and including the first '_' (PREFIX_ style), or "(none)" when there is no
    /// such prefix. Revit does not store the source shared-parameter file, so this
    /// is a heuristic, not the file identity.
    /// </summary>
    public static string PrefixOf(string name)
    {
        var underscore = name.IndexOf('_');
        return underscore > 0 ? name.Substring(0, underscore + 1) : "(none)";
    }

    /// <summary>
    /// The current name of a shared definition, or the GUID itself. Cached per
    /// scan: prefix grouping asks for the same GUID from every family that carries it.
    /// </summary>
    public string NameOf(string guid)
    {
        if (_nameByGuid.TryGetValue(guid, out var cached))
            return cached;

        var name = NameOf(_doc, guid);
        _nameByGuid.Add(guid, name);
        return name;
    }

    /// <summary>The current name of a shared definition, or the GUID itself.</summary>
    public static string NameOf(Document doc, string guid)
    {
        try
        {
            var parsed = Guid.ParseExact(guid, "D");
            return SharedParameterElement.Lookup(doc, parsed)?.Name ?? guid;
        }
        catch (Exception)
        {
            return guid;
        }
    }
}
