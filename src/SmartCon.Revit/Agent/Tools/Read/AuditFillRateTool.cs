using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Batch fill-rate audit over the parameters the issued documentation depends
/// on: the parameter set comes from the fields of the schedules placed on
/// sheets (external benchmark v2, case 8), and the fill counts are computed
/// from ONE collector pass over the model — not from schedule cells and not
/// one pass per parameter. Three separate empty buckets like
/// SummarizeElementsTool.ReadGroupKey: no parameter / HasValue=false / blank
/// string. Read-only.
/// </summary>
public sealed class AuditFillRateTool : AgentReadToolBase
{
    private const int MaxElements = 20000;
    private const int DefaultMaxParameters = 30;
    private const int MaxParameters = 200;
    private const int DefaultMaxCulprits = 20;
    private const int MaxCulprits = 200;

    public AuditFillRateTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "audit_fill_rate",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        TimeoutMs = 60000,
        Description =
            "Batch audit of parameter fill rate before issuing a model. The mandatory parameter " +
            "set is derived from the fields of schedules placed on sheets (issued documentation); " +
            "draft schedules that live outside sheets are reported in offSheetSchedules and are " +
            "NOT audited. Alternatively pass explicit scheduleIds. The summary is computed from " +
            "the model in one collector pass (cap 20000 elements): for every parameter — " +
            "filled / emptyNoValue / emptyString / missingParameter, emptyPct and the culprits " +
            "list (elementId, family, type, levelName). Use it for a quick 'what is empty where' " +
            "report; it never writes anything.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleIds"":{""type"":""array"",""items"":{""type"":""integer""}},""onlyOnSheets"":{""type"":""boolean""},""categories"":{""type"":""array"",""items"":{""type"":""string""}},""includeTypeParameters"":{""type"":""boolean"",""default"":true},""maxCulpritsPerParameter"":{""type"":""integer"",""default"":20,""maximum"":200},""limit"":{""type"":""integer"",""default"":30,""maximum"":200},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var scheduleIds = AgentJson.GetLongArray(args, "scheduleIds");
        var onlyOnSheetsProvided = false;
        var onlyOnSheets = false;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("onlyOnSheets", out var onlyOnSheetsElement)
            && onlyOnSheetsElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            onlyOnSheetsProvided = true;
            onlyOnSheets = onlyOnSheetsElement.GetBoolean();
        }
        var categories = AgentJson.GetStringArray(args, "categories");
        var includeTypeParameters = AgentJson.GetBool(args, "includeTypeParameters", true);
        var maxCulprits = Math.Max(
            1,
            Math.Min(
                AgentJson.GetInt(args, "maxCulpritsPerParameter", DefaultMaxCulprits),
                MaxCulprits));
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultMaxParameters),
            DefaultMaxParameters,
            MaxParameters);

        // Default scope: neither scheduleIds nor onlyOnSheets → on-sheet schedules
        // only (the benchmark contract: "critical = placed on a sheet").
        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(doc);
        var mandatory = new List<ViewSchedule>();
        var notFound = new List<long>();
        var offSheet = new List<Dictionary<string, object?>>();

        foreach (var schedule in AgentScheduleReader.Collect(doc))
        {
            if (schedule.IsTemplate || schedule.IsInternalKeynoteSchedule
                                     || schedule.IsTitleblockRevisionSchedule)
                continue;

            if (scheduleIds.Count > 0)
            {
                if (scheduleIds.Contains(schedule.Id.GetValue()))
                    mandatory.Add(schedule);
                continue;
            }

            sheetNumbers.TryGetValue(schedule.Id.GetValue(), out var numbers);
            var placed = numbers is { Count: > 0 };
            if (placed || onlyOnSheetsProvided && !onlyOnSheets)
                mandatory.Add(schedule);
            else
                offSheet.Add(new Dictionary<string, object?>
                {
                    ["scheduleId"] = schedule.Id.GetValue(),
                    ["name"] = schedule.Name,
                    ["fieldCount"] = CountFields(schedule)
                });
        }

        foreach (var id in scheduleIds)
        {
            if (mandatory.All(item => item.Id.GetValue() != id))
                notFound.Add(id);
        }

        if (mandatory.Count == 0)
        {
            return ToolResult.Fail(
                "no_source_schedules",
                "No schedules matched the requested source set. Pass scheduleIds of existing " +
                "schedules or leave them out to audit every schedule placed on a sheet." +
                (notFound.Count > 0 ? " Unknown scheduleIds: " + string.Join(", ", notFound) : "") +
                " [Action: check list_schedules for ids and sheet placement]");
        }

        // 1. The mandatory parameter set: unique parameter identities from the
        //    fields of the mandatory schedules.
        var parameters = new Dictionary<string, AuditedParameter>(StringComparer.Ordinal);
        var sourceCategories = new HashSet<long>();
        foreach (var schedule in mandatory)
        {
            CollectParameters(doc, schedule, parameters, sourceCategories);
        }

        if (parameters.Count == 0)
        {
            return ToolResult.Fail(
                "no_schedule_parameters",
                "The source schedules carry no parameter fields (only calculated fields); " +
                "there is nothing to audit. [Action: check the schedule fields with " +
                "get_schedule_definition]");
        }

        // 2. One collector pass over the model for ALL parameters.
        var unknownCategories = new List<string>();
        var filterCategories = new HashSet<long>(sourceCategories);
        if (categories.Count > 0)
        {
            var requested = AgentCategoryResolver.Resolve(doc, categories, unknownCategories);
            if (requested.Count == 0)
            {
                return ToolResult.Fail(
                    "unknown_category",
                    "None of the requested categories matched: " +
                    string.Join(", ", unknownCategories.Count > 0 ? unknownCategories : categories) +
                    AgentCategoryResolver.SuggestSuffix(doc, categories[0]) +
                    ". [Action: use a category name from list_categories or an OST_* built-in name]");
            }

            filterCategories.IntersectWith(new HashSet<long>(
                requested.Select(id => id.GetValue())));
        }

        var elements = CollectElements(doc, filterCategories);
        var truncated = elements.Count > MaxElements;
        if (truncated)
            elements.RemoveRange(MaxElements, elements.Count - MaxElements);

        foreach (var element in elements)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var parameter in parameters.Values)
                parameter.Count(doc, element, includeTypeParameters);
        }

        // 3. Worst-first summary with culprits.
        var ordered = parameters.Values
            .OrderByDescending(item => item.Total == 0 ? 0 : item.EmptyCount * 100.0 / item.Total)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var page = AgentPaging.Slice(ordered, offset, limit);
        var hasMore = offset + page.Count < ordered.Count;
        var parameterRows = new List<Dictionary<string, object?>>(page.Count);
        foreach (var parameter in page)
        {
            var emptyPct = parameter.Total == 0
                ? 0
                : Math.Round(parameter.EmptyCount * 100.0 / parameter.Total, 1);
            parameterRows.Add(new Dictionary<string, object?>
            {
                ["parameterName"] = parameter.DisplayName,
                ["guid"] = parameter.Guid,
                ["builtInParameter"] = parameter.BuiltInName,
                ["filled"] = parameter.Filled,
                ["emptyNoValue"] = parameter.EmptyNoValue,
                ["emptyString"] = parameter.EmptyString,
                ["missingParameter"] = parameter.MissingParameter,
                ["total"] = parameter.Total,
                ["emptyPct"] = emptyPct,
                ["culprits"] = parameter.CulpritRows(maxCulprits),
                ["culpritsTruncated"] = parameter.EmptyCount > maxCulprits
            });
        }

        var sourceRows = new List<Dictionary<string, object?>>(mandatory.Count);
        foreach (var schedule in mandatory)
        {
            sheetNumbers.TryGetValue(schedule.Id.GetValue(), out var numbers);
            sourceRows.Add(new Dictionary<string, object?>
            {
                ["scheduleId"] = schedule.Id.GetValue(),
                ["name"] = schedule.Name,
                ["sheetNumbers"] = numbers ?? Array.Empty<string>()
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["mode"] = scheduleIds.Count > 0
                ? "scheduleIds"
                : onlyOnSheetsProvided && !onlyOnSheets ? "allSchedules" : "onSheetSchedules",
            ["parameters"] = parameterRows,
            ["parameterCount"] = ordered.Count,
            ["returned"] = parameterRows.Count,
            ["offset"] = offset,
            ["nextOffset"] = hasMore ? offset + page.Count : (int?)null,
            ["truncated"] = hasMore,
            ["scannedElements"] = elements.Count,
            ["elementsTruncated"] = truncated,
            ["includeTypeParameters"] = includeTypeParameters,
            ["sourceSchedules"] = sourceRows,
            ["offSheetSchedules"] = offSheet.Count > 0 ? offSheet : null,
            ["unknownScheduleIds"] = notFound.Count > 0 ? notFound : null,
            ["unknownCategories"] = unknownCategories.Count > 0 ? unknownCategories : null,
            ["hint"] =
                "emptyNoValue = parameter exists but has no value; emptyString = blank string; " +
                "missingParameter = the element (and its type) does not carry the parameter. " +
                "The parameter set comes from the fields of sourceSchedules; offSheetSchedules " +
                "are drafts outside sheets and are not audited."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Audited " + ordered.Count + " parameter(s) over " + elements.Count +
            " element(s) from " + mandatory.Count + " source schedule(s)" +
            (offSheet.Count > 0 ? "; " + offSheet.Count + " draft schedule(s) off sheets skipped" : "") +
            (hasMore ? "; next offset " + (offset + page.Count) : "") + ".");
    }

    private static int CountFields(ViewSchedule schedule)
    {
        try
        {
            return schedule.Definition.GetFieldOrder().Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Unique parameter identities from schedule fields: shared by GUID, built-in
    /// by enum name; calculated fields (no parameter id) are skipped.
    /// </summary>
    private static void CollectParameters(
        Document doc,
        ViewSchedule schedule,
        Dictionary<string, AuditedParameter> parameters,
        HashSet<long> sourceCategories)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            var categoryId = definition.CategoryId;
            // Category ids of built-in categories are NEGATIVE (OST_Walls et al);
            // only the invalid id means "no category restriction".
            if (categoryId is not null && categoryId != ElementId.InvalidElementId)
                sourceCategories.Add(categoryId.GetValue());
        }
        catch (Exception)
        {
            // Multi-category schedules keep the element scope unrestricted.
        }

        IList<ScheduleFieldId> order;
        try
        {
            order = definition.GetFieldOrder();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var fieldId in order)
        {
            ScheduleField? field;
            try
            {
                field = definition.GetField(fieldId);
            }
            catch (Exception)
            {
                continue;
            }

            if (field is null)
                continue;

            long parameterId;
            try
            {
                var raw = field.ParameterId;
                if (raw is null || raw == ElementId.InvalidElementId)
                    continue;

                parameterId = raw.GetValue();
            }
            catch (Exception)
            {
                // Calculated/combined fields have no parameter id.
                continue;
            }

            if (parameterId > 0)
            {
                var guid = AgentScheduleReader.DescribeParameterGuid(doc, field);
                if (guid is null)
                    continue;

                if (!parameters.ContainsKey(guid))
                {
                    var name = AgentScheduleReader.DescribeParameter(doc, field);
                    parameters.Add(guid, new AuditedParameter(
                        name ?? guid, guid, null, Guid.ParseExact(guid, "D")));
                }
            }
            else
            {
                var builtIn = (BuiltInParameter)(int)parameterId;
                var enumName = builtIn.ToString();
                if (parameters.ContainsKey(enumName))
                    continue;

                var label = AgentScheduleReader.DescribeParameter(doc, field) ?? enumName;
                parameters.Add(enumName, new AuditedParameter(label, null, enumName, null));
            }
        }
    }

    private static List<Element> CollectElements(Document doc, HashSet<long> categoryIds)
    {
        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        if (categoryIds.Count > 0)
        {
            // Several WherePasses calls combine as AND: multiple categories need
            // an explicit OR filter or the result is silently empty.
            var filters = new List<ElementFilter>(categoryIds.Count);
            foreach (var id in categoryIds)
                filters.Add(new ElementCategoryFilter(ElementIdCompat.Create(id)));
            collector.WherePasses(categoryIds.Count == 1
                ? filters[0]
                : new LogicalOrFilter(filters));
            return collector.ToElements().ToList();
        }

        // No category restriction (multi-category sources): keep it to model
        // elements by dropping the view/document infrastructure categories.
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Views), inverted: true));
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Sheets), inverted: true));
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Schedules), inverted: true));
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Viewports), inverted: true));
        return collector.ToElements().ToList();
    }

    private sealed class AuditedParameter
    {
        private readonly string? _guidValue;
        private readonly Guid? _guid;
        private readonly BuiltInParameter? _builtIn;
        private readonly List<Culprit> _culprits = new();

        public AuditedParameter(
            string displayName,
            string? guid,
            string? builtInName,
            Guid? guidValue)
        {
            DisplayName = displayName;
            BuiltInName = builtInName;
            _guid = guidValue;
            _guidValue = guid;
            _builtIn = builtInName is null
                ? null
                : (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), builtInName, true);
        }

        public string DisplayName { get; }

        public string? Guid => _guidValue;

        public string? BuiltInName { get; }

        public int Filled { get; private set; }

        public int EmptyNoValue { get; private set; }

        public int EmptyString { get; private set; }

        public int MissingParameter { get; private set; }

        public int Total => Filled + EmptyNoValue + EmptyString + MissingParameter;

        public int EmptyCount => EmptyNoValue + EmptyString + MissingParameter;

        public void Count(Document doc, Element element, bool includeTypeParameters)
        {
            var parameter = Resolve(doc, element, includeTypeParameters, out var owner);
            if (parameter is null)
            {
                MissingParameter++;
                AddCulprit(element);
                return;
            }

            if (!parameter.HasValue)
            {
                EmptyNoValue++;
                AddCulprit(element);
                return;
            }

            var text = ReadText(parameter);
            if (string.IsNullOrWhiteSpace(text))
            {
                EmptyString++;
                AddCulprit(element);
            }
            else
            {
                Filled++;
            }
        }

        private Parameter? Resolve(
            Document doc,
            Element element,
            bool includeTypeParameters,
            out Element owner)
        {
            owner = element;
            var parameter = _guid is not null
                ? element.get_Parameter(_guid.Value)
                : element.get_Parameter(_builtIn!.Value);
            if (parameter is not null && parameter.HasValue)
                return parameter;

            if (!includeTypeParameters)
                return parameter;

            // Type-bound shared parameters expose a NON-NULL but EMPTY view on
            // instances (live case 8 #468): fall through to the type whenever
            // the instance view carries no value.
            var typeId = element.GetTypeId();
            if (typeId is null || typeId == ElementId.InvalidElementId)
                return parameter;

            var typeElement = doc.GetElement(typeId);
            if (typeElement is null)
                return parameter;

            var typeParameter = _guid is not null
                ? typeElement.get_Parameter(_guid.Value)
                : typeElement.get_Parameter(_builtIn!.Value);
            if (typeParameter is not null && typeParameter.HasValue)
            {
                owner = typeElement;
                return typeParameter;
            }

            // Honest empties: keep the instance view so real holes (both empty)
            // and instance-bound parameters still count on the element itself.
            return parameter ?? typeParameter;
        }

        private static string? ReadText(Parameter parameter)
        {
            try
            {
                return parameter.StorageType == StorageType.String
                    ? parameter.AsString()
                    : parameter.AsValueString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void AddCulprit(Element element)
        {
            if (_culprits.Count >= MaxCulprits * 4)
                return;

            _culprits.Add(new Culprit(
                element.Id.GetValue(),
                FamilyOf(element),
                TypeOf(element),
                LevelOf(element)));
        }

        public List<Dictionary<string, object?>> CulpritRows(int max)
        {
            var rows = new List<Dictionary<string, object?>>(Math.Min(max, _culprits.Count));
            foreach (var culprit in _culprits.Take(max))
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["elementId"] = culprit.ElementId,
                    ["family"] = culprit.Family,
                    ["type"] = culprit.Type,
                    ["levelName"] = culprit.LevelName
                });
            }

            return rows;
        }

        private static string? FamilyOf(Element element) =>
            element is FamilyInstance instance ? instance.Symbol?.Family?.Name : null;

        private static string? TypeOf(Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                return typeId is null || typeId == ElementId.InvalidElementId
                    ? null
                    : element.Document.GetElement(typeId)?.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? LevelOf(Element element)
        {
            try
            {
                var levelId = element.LevelId;
                if (levelId is null || levelId == ElementId.InvalidElementId)
                    return null;

                return element.Document.GetElement(levelId) is Level level ? level.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private sealed class Culprit
    {
        public Culprit(long elementId, string? family, string? type, string? levelName)
        {
            ElementId = elementId;
            Family = family;
            Type = type;
            LevelName = levelName;
        }

        public long ElementId { get; }

        public string? Family { get; }

        public string? Type { get; }

        public string? LevelName { get; }
    }
}
