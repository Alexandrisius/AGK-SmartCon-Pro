using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Reverse usage index of shared/project parameters: which schedules and view
/// filters reference them (v16 field feedback: the audit used to cost one
/// list_schedules call per parameter). One call
/// takes names[], guids[] or a name prefix and returns, per parameter,
/// the schedules (with the field heading and hidden flag) and the filters that
/// use it, plus the project-binding context when one exists.
/// </summary>
public sealed class GetParameterUsageTool : AgentReadToolBase
{
    private const int MaxParametersPerCall = 60;

    private const int MaxSchedulesPerParameter = 50;

    private const int MaxFiltersPerParameter = 50;

    private const int MaxFilterDepth = 8;

    public GetParameterUsageTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_parameter_usage",
        Description =
            "Reverse usage index: for each requested parameter (names[], guids[] or a name " +
            "PREFIX such as a vendor prefix) — which SCHEDULES carry it as a field (schedule id, " +
            "name, field heading, isFieldHidden) and which view FILTERS reference it in a rule " +
            "(filter id, name), plus the project-binding context (scope, categories) when the " +
            "parameter is bound at project level. One call replaces a per-name or per-GUID " +
            "list_schedules sweep (a 50-parameter audit used to be 50 calls). Definitions " +
            "are resolved from the document's ParameterElements (shared parameters embedded by " +
            "loaded families included) and from project bindings by name; a name with neither " +
            "is reported as notFound=true — it may still live in family documents " +
            "(find_families_with_parameter). Schedule fields are matched by parameter id and by " +
            "column heading; filter rules by rule parameter id and definition name.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson +
            @"""names"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":60,""description"":""Parameter names to audit (exact, case-insensitive)""},""guids"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":60,""description"":""Shared parameter GUIDs — the safe key when names repeat""},""namePrefix"":{""type"":""string"",""description"":""Audit every parameter whose name starts with this prefix: the document's shared definitions plus project bindings are scanned for the prefix""},""limit"":{""type"":""integer"",""default"":60,""minimum"":1,""maximum"":60,""description"":""Page size when namePrefix matches many parameters""},""offset"":{""type"":""integer"",""default"":0,""description"":""Page through a wide namePrefix sweep""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var guids = ParseGuids(args, out var guidError);
        if (guidError is not null)
            return ToolResult.Fail("invalid_arguments", guidError);
        var names = ParseNames(args, "names");
        var prefix = AgentJson.GetString(args, "namePrefix")?.Trim();

        if (guids.Count == 0 && names.Count == 0 && string.IsNullOrWhiteSpace(prefix))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Pass names[], guids[] or namePrefix (at least one target).");
        }

        if (guids.Count + names.Count > MaxParametersPerCall)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "At most " + MaxParametersPerCall + " parameters per call; split the list. " +
                "[Action: page the audit by alphabet or use namePrefix]");
        }

        // Targets: shared definitions from the document's ParameterElements
        // (covers family-embedded shared parameters without a project binding)
        // and project-binding rows by name.
        var targets = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ParameterElement)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not SharedParameterElement shared)
                continue;

            var definitionName = shared.GetDefinition().Name;
            var guid = shared.GuidValue;
            var wanted = guids.Contains(guid)
                         || names.Contains(definitionName, StringComparer.OrdinalIgnoreCase)
                         || (prefix is { Length: > 0 }
                             && SmartCon.Core.Services.Agent.AgentFamilyText.NormalizeForMatch(definitionName)
                                 .StartsWith(
                                     SmartCon.Core.Services.Agent.AgentFamilyText.NormalizeForMatch(prefix),
                                     StringComparison.Ordinal));
            if (!wanted)
                continue;

            var key = guid.ToString("D");
            targets[key] = new Target
            {
                Key = key,
                Name = definitionName,
                Guid = guid,
                DefinitionId = shared.Id.GetValue()
            };
        }

        foreach (var row in AgentProjectParameterReader.Collect(doc))
        {
            var byGuid = row.Guid is not null && guids.Contains(row.Guid.Value);
            var byName = names.Contains(row.Name, StringComparer.OrdinalIgnoreCase);
            var byPrefix = prefix is { Length: > 0 }
                           && SmartCon.Core.Services.Agent.AgentFamilyText.NormalizeForMatch(row.Name)
                               .StartsWith(
                                   SmartCon.Core.Services.Agent.AgentFamilyText.NormalizeForMatch(prefix),
                                   StringComparison.Ordinal);
            if (!byGuid && !byName && !byPrefix)
                continue;

            var key = row.Guid?.ToString("D") ?? "name:" + row.Name;
            if (targets.TryGetValue(key, out var existing))
            {
                existing.ProjectScope = row.BindingType;
                existing.ProjectCategories = row.Categories;
                existing.DefinitionId = row.DefinitionId;
            }
            else
            {
                targets[key] = new Target
                {
                    Key = key,
                    Name = row.Name,
                    Guid = row.Guid,
                    DefinitionId = row.DefinitionId,
                    ProjectScope = row.BindingType,
                    ProjectCategories = row.Categories
                };
            }
        }

        var requestedNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var name in requestedNames)
        {
            if (targets.Values.Any(target =>
                    string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            targets["name:" + name] = new Target
            {
                Key = "name:" + name,
                Name = name,
                NotFound = true
            };
        }

        foreach (var guid in guids)
        {
            var key = guid.ToString("D");
            if (!targets.ContainsKey(key))
            {
                targets[key] = new Target
                {
                    Key = key,
                    Name = "(no definition with this GUID in the document)",
                    Guid = guid,
                    NotFound = true
                };
            }
        }

        // A wide prefix can match hundreds of parameters: page the audit
        // instead of failing (live feedback v17, 2.1).
        var limit = Math.Max(
            1,
            Math.Min(
                AgentJson.GetInt(args, "limit", MaxParametersPerCall),
                MaxParametersPerCall));
        var pageOffset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var totalMatched = targets.Count;
        var page = targets.Values
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(pageOffset)
            .Take(limit)
            .ToList();
        var pageNextOffset = pageOffset + page.Count < totalMatched
            ? pageOffset + page.Count
            : (int?)null;

        var schedulesById = CollectSchedules(doc, page, ct);
        var filtersById = CollectFilters(doc, page, ct);
        foreach (var target in page)
        {
            schedulesById.TryGetValue(target.Key, out var schedules);
            filtersById.TryGetValue(target.Key, out var filters);
            target.Schedules = schedules ?? new List<object>();
            target.Filters = filters ?? new List<object>();
        }

        var ordered = page;
        var usedAnywhere = ordered.Count(target =>
            target.Schedules.Count > 0 || target.Filters.Count > 0);
        var payload = new Dictionary<string, object?>
        {
            ["parameters"] = ordered.Select(target => new Dictionary<string, object?>
            {
                ["name"] = target.Name,
                ["guid"] = target.Guid?.ToString("D"),
                ["notFound"] = target.NotFound ? true : null,
                ["projectBinding"] = target.ProjectScope is null
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["scope"] = target.ProjectScope,
                        ["categories"] = target.ProjectCategories
                    },
                ["scheduleCount"] = target.Schedules.Count,
                ["schedulesTruncated"] = target.Schedules.Count >= MaxSchedulesPerParameter,
                ["schedules"] = target.Schedules,
                ["filterCount"] = target.Filters.Count,
                ["filtersTruncated"] = target.Filters.Count >= MaxFiltersPerParameter,
                ["filters"] = target.Filters
            }).ToList(),
            ["parameterCount"] = ordered.Count,
            ["totalMatched"] = totalMatched,
            ["offset"] = pageOffset,
            ["nextOffset"] = pageNextOffset,
            ["usedAnywhereCount"] = usedAnywhere,
            ["note"] =
                "Schedules are matched by field parameter id and by column heading; filters by " +
                "rule parameter id and definition name. notFound means neither a document " +
                "definition nor a project binding carries the name/GUID — the parameter may " +
                "still live inside family documents (find_families_with_parameter). " +
                "Templates are skipped."
        };

        var messageText = "Parameter usage: " + ordered.Count + " of " + totalMatched +
                          " parameter(s) audited" +
                          (prefix is { Length: > 0 } ? " (prefix '" + prefix + "')" : string.Empty) +
                          (pageNextOffset is not null
                              ? ", next offset " + pageNextOffset
                              : string.Empty) +
                          "; " + usedAnywhere + " used in schedules/filters" +
                          (ordered.Count - usedAnywhere > 0
                              ? ", " + (ordered.Count - usedAnywhere) + " unused"
                              : string.Empty) + ".";
        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private static List<Guid> ParseGuids(JsonElement args, out string? error)
    {
        error = null;
        var result = new List<Guid>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("guids", out var value))
        {
            return result;
        }

        if (value.ValueKind == JsonValueKind.String
            && TryExpandStringifiedArray(value.GetString(), out var expanded))
        {
            value = expanded;
        }

        if (value.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            if (!Guid.TryParse(text!, out var guid))
            {
                error = "'" + text + "' is not a valid GUID.";
                return new List<Guid>();
            }

            result.Add(guid);
        }

        return result;
    }

    private static List<string> ParseNames(JsonElement args, string property)
    {
        var result = new List<string>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value))
        {
            return result;
        }

        if (value.ValueKind == JsonValueKind.String
            && TryExpandStringifiedArray(value.GetString(), out var expanded))
        {
            value = expanded;
        }

        if (value.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim()
                : null;
            if (!string.IsNullOrEmpty(text))
                result.Add(text!);
        }

        return result;
    }

    /// <summary>
    /// LLM clients sometimes emit an array as one JSON STRING ("[\"A\",\"B\"]");
    /// the category resolver already compensates for that quirk, so the usage
    /// index does too (validator v16, P3).
    /// </summary>
    private static bool TryExpandStringifiedArray(string? raw, out JsonElement expanded)
    {
        expanded = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw!.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
            return false;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            expanded = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Dictionary<string, List<object>> CollectSchedules(
        Document doc,
        IEnumerable<Target> targets,
        CancellationToken ct)
    {
        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
            {
                ct.ThrowIfCancellationRequested();
                if (element is not ViewSchedule schedule || schedule.IsTemplate)
                    continue;

                var definition = schedule.Definition;
                for (var index = 0; index < definition.GetFieldCount(); index++)
                {
                    ScheduleField? field;
                    try
                    {
                        field = definition.GetField(index);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (field is null)
                        continue;

                    var fieldParameterId = field.ParameterId?.GetValue();
                    foreach (var target in targets)
                    {
                        if (target.NotFound)
                            continue;

                        var byId = target.DefinitionId.HasValue
                                   && fieldParameterId == target.DefinitionId.Value;
                        var byName = !byId && string.Equals(
                            field.ColumnHeading, target.Name, StringComparison.OrdinalIgnoreCase);
                        if (!byId && !byName)
                            continue;

                        if (!result.TryGetValue(target.Key, out var list))
                        {
                            list = new List<object>();
                            result.Add(target.Key, list);
                        }

                        if (list.Count < MaxSchedulesPerParameter)
                        {
                            list.Add(new Dictionary<string, object?>
                            {
                                ["scheduleId"] = schedule.Id.GetValue(),
                                ["name"] = schedule.Name,
                                ["fieldHeading"] = field.ColumnHeading,
                                ["isFieldHidden"] = field.IsHidden
                            });
                        }

                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Report what was readable — same policy as the removal audit.
        }

        return result;
    }

    private static Dictionary<string, List<object>> CollectFilters(
        Document doc,
        IEnumerable<Target> targets,
        CancellationToken ct)
    {
        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var element in new FilteredElementCollector(doc)
                         .OfClass(typeof(ParameterFilterElement)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (element is not ParameterFilterElement filter)
                        continue;

                    var elementFilter = filter.GetElementFilter();
                    foreach (var target in targets)
                    {
                        if (target.NotFound || !FilterUsesParameter(elementFilter, doc, target))
                            continue;

                        if (!result.TryGetValue(target.Key, out var list))
                        {
                            list = new List<object>();
                            result.Add(target.Key, list);
                        }

                        if (list.Count < MaxFiltersPerParameter)
                        {
                            list.Add(new Dictionary<string, object?>
                            {
                                ["filterId"] = filter.Id.GetValue(),
                                ["name"] = filter.Name
                            });
                        }
                    }
                }
                catch (Exception)
                {
                    // One unreadable filter must not hide the rest (validator v16, P3).
                }
            }
        }
        catch (Exception)
        {
            // Report what was readable.
        }

        return result;
    }

    private static bool FilterUsesParameter(
        ElementFilter? filter,
        Document doc,
        Target target,
        int depth = 0)
    {
        if (filter is null || depth > MaxFilterDepth)
            return false;

        if (filter is ElementLogicalFilter logical)
        {
            foreach (var child in logical.GetFilters())
            {
                if (FilterUsesParameter(child, doc, target, depth + 1))
                    return true;
            }

            return false;
        }

        if (filter is not ElementParameterFilter parameterFilter)
            return false;

        foreach (var rule in parameterFilter.GetRules())
        {
            try
            {
                var parameterId = rule.GetRuleParameter();
                if (parameterId is null || parameterId == ElementId.InvalidElementId)
                    continue;

                if (target.DefinitionId.HasValue
                    && parameterId.GetValue() == target.DefinitionId.Value)
                {
                    return true;
                }

                if (doc.GetElement(parameterId) is ParameterElement ruleParameter
                    && string.Equals(
                        ruleParameter.GetDefinition().Name, target.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Skip unreadable rules.
            }
        }

        return false;
    }

    private sealed class Target
    {
        public required string Key { get; init; }

        public required string Name { get; init; }

        public Guid? Guid { get; init; }

        public long? DefinitionId { get; set; }

        public string? ProjectScope { get; set; }

        public IReadOnlyList<string>? ProjectCategories { get; set; }

        public bool NotFound { get; init; }

        public List<object> Schedules { get; set; } = new();

        public List<object> Filters { get; set; } = new();
    }
}
