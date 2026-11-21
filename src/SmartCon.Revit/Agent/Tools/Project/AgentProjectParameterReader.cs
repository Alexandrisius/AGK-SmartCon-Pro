using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Project;

/// <summary>
/// Shared reader for project parameter bindings (doc.ParameterBindings): one
/// row per definition bound in the project. Used by list_project_parameters,
/// the read-back of add_project_shared_parameter and the undo of the binding
/// change items.
/// </summary>
internal static class AgentProjectParameterReader
{
    internal sealed class ProjectParameterRow
    {
        public string Name { get; set; } = string.Empty;

        public long DefinitionId { get; set; }

        public bool IsShared { get; set; }

        public Guid? Guid { get; set; }

        public string BindingType { get; set; } = "instance";

        public List<string> Categories { get; set; } = new();

        public List<long> CategoryIds { get; set; } = new();

        public string? ParameterGroup { get; set; }

        public bool Visible { get; set; } = true;
    }

    /// <summary>
    /// Serializable binding state recorded in change items: display categories
    /// for the audit trail plus raw category ids for the undo re-bind.
    /// </summary>
    internal sealed class BindingSnapshot
    {
        public string Name { get; set; } = string.Empty;

        public string BindingType { get; set; } = "instance";

        public List<string> Categories { get; set; } = new();

        public List<long> CategoryIds { get; set; } = new();
    }

    internal static List<ProjectParameterRow> Collect(Document doc)
    {
        var rows = new List<ProjectParameterRow>();
        var iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition definition
                || string.IsNullOrEmpty(definition.Name)
                || iterator.Current is not ElementBinding binding)
            {
                continue;
            }

            var categories = new List<string>();
            var categoryIds = new List<long>();
            foreach (Category category in binding.Categories)
            {
                categories.Add(category.Name ?? string.Empty);
                categoryIds.Add(category.Id.GetValue());
            }

            var isShared = false;
            Guid? guid = null;
            try
            {
                if (doc.GetElement(definition.Id) is SharedParameterElement shared)
                {
                    isShared = true;
                    guid = shared.GuidValue;
                }
            }
            catch (Exception)
            {
                // A definition without a resolvable parameter element stays non-shared.
            }

            var visible = true;
            try
            {
                visible = definition.Visible;
            }
            catch (Exception)
            {
                // Built-in definitions may hide the flag; keep the default.
            }

            rows.Add(new ProjectParameterRow
            {
                Name = definition.Name!,
                DefinitionId = definition.Id.GetValue(),
                IsShared = isShared,
                Guid = guid,
                BindingType = binding is InstanceBinding ? "instance" : "type",
                Categories = categories,
                CategoryIds = categoryIds,
                ParameterGroup = AgentFamilySpecs.GroupName(definition),
                Visible = visible
            });
        }

        rows.Sort((left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(
                    left.Guid?.ToString("N") ?? string.Empty,
                    right.Guid?.ToString("N") ?? string.Empty,
                    StringComparison.Ordinal);
        });

        return rows;
    }

    internal static List<ProjectParameterRow> FindAll(Document doc, string name) =>
        Collect(doc)
            .Where(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Locates the live mapping (definition + binding) of a recorded change item:
    /// by definition id first, falling back to the parameter name.
    /// </summary>
    internal static bool TryGetMapping(
        Document doc,
        long definitionId,
        string name,
        out InternalDefinition? definition,
        out ElementBinding? binding)
    {
        InternalDefinition? byId = null;
        ElementBinding? byIdBinding = null;
        InternalDefinition? byName = null;
        ElementBinding? byNameBinding = null;

        var iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition key || iterator.Current is not ElementBinding current)
                continue;

            if (definitionId != 0 && key.Id.GetValue() == definitionId)
            {
                byId = key;
                byIdBinding = current;
                break;
            }

            if (byName is null && string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                byName = key;
                byNameBinding = current;
            }
        }

        definition = byId ?? byName;
        binding = byId is not null ? byIdBinding : byNameBinding;
        return definition is not null && binding is not null;
    }

    internal static string SerializeSnapshot(BindingSnapshot snapshot) =>
        AgentJson.Serialize(snapshot).GetRawText();

    internal static BindingSnapshot? TryParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<BindingSnapshot>(
                json!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static BindingSnapshot SnapshotOf(ProjectParameterRow row) =>
        new()
        {
            Name = row.Name,
            BindingType = row.BindingType,
            Categories = row.Categories,
            CategoryIds = row.CategoryIds
        };
}
