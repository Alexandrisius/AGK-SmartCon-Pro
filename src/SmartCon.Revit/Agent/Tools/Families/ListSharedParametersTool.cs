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
            "Filters: group, nameContains; paged with limit/offset.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""group"":{""type"":""string"",""description"":""Group name or its part (case-insensitive)""},""nameContains"":{""type"":""string""},""limit"":{""type"":""integer"",""default"":200,""maximum"":1000},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

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
                ". [Action: configure a shared parameter file in Manage > Shared Parameters]");
        }

        if (file is null)
        {
            return ToolResult.Fail(
                "shared_parameter_file_missing",
                "No active shared parameter file is configured. [Action: Manage > Shared " +
                "Parameters > Create/Browse, then retry]");
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
                ". [Action: check the file and retry]");
        }

        matches.Sort((left, right) =>
        {
            var byGroup = string.Compare(
                left.Group, right.Group, StringComparison.OrdinalIgnoreCase);
            return byGroup != 0
                ? byGroup
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

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