using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Reads the active shared parameter file (ФОП) so the model can choose an exact
/// definition before add_family_parameter(isShared=true) instead of guessing
/// names. Read-only: opening the file never changes it.
/// </summary>
public sealed class ListSharedParametersTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    /// <summary>
    /// set_shared_parameter_file lives in the params pack, not in families:
    /// the action names the pack and the loader call, otherwise a model that
    /// only loaded families sees a tool it cannot call (harness feedback).
    /// </summary>
    private const string MissingFileAction =
        " [Action: call load_tool_pack(pack=\"params\") first - set_shared_parameter_file is " +
        "in the params pack, not families - then set_shared_parameter_file(path=<absolute .txt " +
        "path>, dryRun=true); createIfMissing=true creates a new empty file, an existing file " +
        "is not overwritten; the path is named by the user. In the Revit UI: Manage > Shared " +
        "Parameters]";

    public ListSharedParametersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_shared_parameters",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Reads the active shared parameter file (ФОП): its path, groups and every " +
            "definition with name, GUID, data type, parameter group, visible and " +
            "userModifiable flags. Use it to pick the exact definition before " +
            "add_family_parameter(isShared=true) or to check which ФОП is active. " +
            "Filters: group, nameContains; paged with limit/offset. summary=true " +
            "returns the inventory without pages: byPrefix[] and byGroup[] counters " +
            "over the whole (filtered) file.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""group"":{""type"":""string"",""description"":""Group name or its part (case-insensitive)""},""nameContains"":{""type"":""string""},""summary"":{""type"":""boolean"",""default"":false,""description"":""Inventory in one call: byPrefix[] (the part of the name before '_'), byGroup[] with definition counts - no pages to read""},""limit"":{""type"":""integer"",""default"":200,""maximum"":1000},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    public override bool SupportsDocumentRouting => false;

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var groupFilter = AgentJson.GetString(args, "group");
        var nameContains = AgentJson.GetString(args, "nameContains");
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));

        DefinitionFile? file;
        try
        {
            file = doc.Application.OpenSharedParameterFile();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "shared_parameter_file_missing",
                "The active shared parameter file could not be opened: " + ex.Message +
                MissingFileAction);
        }

        if (file is null)
        {
            return ToolResult.Fail(
                "shared_parameter_file_missing",
                "No active shared parameter file is configured." + MissingFileAction);
        }

        var matches = new List<SharedParameterRow>();
        var groupsCount = 0;
        try
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                ct.ThrowIfCancellationRequested();
                groupsCount++;

                var groupName = group.Name ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(groupFilter)
                    && groupName.IndexOf(groupFilter!, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                foreach (Definition definition in group.Definitions)
                {
                    ct.ThrowIfCancellationRequested();
                    if (definition is not ExternalDefinition external)
                        continue;

                    var name = external.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(nameContains)
                        && name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matches.Add(new SharedParameterRow
                    {
                        Group = groupName,
                        Name = name,
                        Guid = external.GUID,
                        Spec = AgentFamilySpecs.SpecName(external),
                        ParameterGroup = AgentFamilySpecs.GroupName(external),
                        Visible = external.Visible,
                        UserModifiable = external.UserModifiable,
                        Description = string.IsNullOrWhiteSpace(external.Description)
                            ? null
                            : external.Description
                    });
                }
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "shared_parameter_file_missing",
                "The shared parameter file could not be read: " + ex.Message +
                MissingFileAction);
        }

        matches.Sort((left, right) =>
        {
            var byGroup = string.Compare(
                left.Group, right.Group, StringComparison.OrdinalIgnoreCase);
            return byGroup != 0
                ? byGroup
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        // The inventory in one call: prefix/group counters over the whole
        // (filtered) file instead of paging through 130 definitions (S.48, #533).
        if (AgentJson.GetBool(args, "summary", false))
        {
            var byPrefix = matches
                .GroupBy(row => AgentSharedParameterResolver.PrefixOf(row.Name))
                .Select(group => new { prefix = group.Key, count = group.Count() })
                .OrderByDescending(entry => entry.count)
                .ThenBy(entry => entry.prefix, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var byGroup = matches
                .GroupBy(row => row.Group)
                .Select(group => new { group = group.Key, count = group.Count() })
                .OrderByDescending(entry => entry.count)
                .ThenBy(entry => entry.group, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return ToolResult.Ok(
                AgentJson.Serialize(new Dictionary<string, object?>
                {
                    ["filePath"] = file.Filename,
                    ["groupsCount"] = groupsCount,
                    ["total"] = matches.Count,
                    ["byPrefix"] = byPrefix,
                    ["byGroup"] = byGroup,
                    ["note"] = "summary=true: counters over the whole (filtered) file; the " +
                               "prefix is the part of the definition name before '_'. Read the " +
                               "definitions of one bucket with nameContains=<prefix>."
                }),
                "Shared parameter file '" + file.Filename + "': " + matches.Count +
                " definition(s), " + byPrefix.Count + " prefix(es), " + byGroup.Count + " group(s).");
        }

        var page = new List<Dictionary<string, object?>>();
        for (var index = offset; index < matches.Count && page.Count < limit; index++)
        {
            var row = matches[index];
            page.Add(new Dictionary<string, object?>
            {
                ["group"] = row.Group,
                ["name"] = row.Name,
                ["guid"] = row.Guid.ToString("D"),
                ["spec"] = row.Spec,
                ["parameterGroup"] = row.ParameterGroup,
                ["visible"] = row.Visible,
                ["userModifiable"] = row.UserModifiable,
                ["description"] = row.Description
            });
        }

        var nextOffset = offset + page.Count;
        var payload = new Dictionary<string, object?>
        {
            ["filePath"] = file.Filename,
            ["groupsCount"] = groupsCount,
            ["total"] = matches.Count,
            ["parameters"] = page,
            ["returned"] = page.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < matches.Count ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < matches.Count,
            ["hint"] = "Pass the exact name or guid to add_family_parameter(isShared=true); " +
                       "createDefinition stays false by default, so a missing definition is " +
                       "reported instead of being added to this file."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            matches.Count == 0
                ? "The active shared parameter file contains no definitions matching the request."
                : "Read " + page.Count + " of " + matches.Count + " shared parameter " +
                  "definition(s) from " + file.Filename + ".");
    }

    private sealed class SharedParameterRow
    {
        public required string Group { get; init; }

        public required string Name { get; init; }

        public Guid Guid { get; init; }

        public string? Spec { get; init; }

        public string? ParameterGroup { get; init; }

        public bool Visible { get; init; }

        public bool UserModifiable { get; init; }

        public string? Description { get; init; }
    }
}
