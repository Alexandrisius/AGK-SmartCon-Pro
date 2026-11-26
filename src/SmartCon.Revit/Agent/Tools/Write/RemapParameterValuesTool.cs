using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class RemapParameterValuesTool : AgentWriteToolBase
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 500;
    private const int MaxMappings = 20;
    private const int MaxOffset = 1_000_000;

    public RemapParameterValuesTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "remap_parameter_values",
        Description =
            "Copies values from a source parameter into a target parameter (typical shared " +
            "parameter migration: old ФОП parameter → new one). mapping: [{source:{name|guid," +
            "scope}, target:{name|guid,scope}}], at most 20 pairs. filter: {categories, " +
            "levelIds, typeIds, familyIds, familyNames, limit} selects the elements; categories " +
            "are BuiltInCategory tokens (OST_Pipes) or category display names; familyNames is " +
            "the stable key for placed instances of loadable families (familyId changes on " +
            "every reload). offset pages past the first limit elements (the pair is re-run " +
            "until hasMore=false). Elements without the source value are skipped; storage-type " +
            "mismatches are converted when possible. dryRun defaults to true. This is also the " +
            "path for INSTANCE parameter values of placed elements — map_family_parameters " +
            "copies type defaults only.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""mapping"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""object"",""properties"":{""source"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""guid"":{""type"":""string""},""scope"":{""type"":""string""}},""additionalProperties"":false},""target"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""guid"":{""type"":""string""},""scope"":{""type"":""string""}},""additionalProperties"":false}},""required"":[""target""],""additionalProperties"":false}},""filter"":{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},""familyIds"":{""type"":""array"",""items"":{""type"":""integer"",""maxItems"":50,""description"":""Placed instances of these loadable families; combine with familyNames""}},""familyNames"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":50,""description"":""Placed instances of these loadable families by NAME (the stable key — familyId changes on every reload); every name must resolve""},""limit"":{""type"":""integer"",""default"":500}},""additionalProperties"":false},""offset"":{""type"":""integer"",""default"":0,""description"":""Skip the first N matching elements (paging with limit; repeat until hasMore=false)""},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""mapping""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<RemapRequest>(args);
        var mapping = request?.Mapping;
        if (mapping is null || mapping.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "mapping is empty; pass at least one {source,target} pair.";
            return;
        }

        if (mapping.Count > MaxMappings)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxMappings + " mapping pairs per call are allowed.";
            return;
        }

        var filter = request!.Filter ?? new RemapFilter();
        var limit = Math.Max(1, Math.Min(filter.Limit ?? DefaultLimit, MaxLimit));
        // Clamped: a caller-provided huge offset must not preallocate the
        // element list into gigabytes (validator note: LLM can send 10^9).
        var offset = Math.Max(0, Math.Min(AgentJson.GetInt(args, "offset", 0), MaxOffset));
        var atomic = AgentJson.GetBool(args, "atomic", false);

        if (!TryResolveCategories(doc, filter.Categories, out var categories, out var categoryError,
                out var categoryReason))
        {
            outcome.Success = false;
            outcome.ErrorCode = categoryError;
            outcome.Message = categoryReason;
            return;
        }
        if (!TryResolveFamilyFilter(doc, filter, out var familyFilter, out var familyError,
                out var familyReason))
        {
            outcome.Success = false;
            outcome.ErrorCode = familyError;
            outcome.Message = familyReason;
            return;
        }
        var levelFilter = new HashSet<long>(filter.LevelIds ?? new List<long>());
        var typeFilter = new HashSet<long>(filter.TypeIds ?? new List<long>());

        // One extra match past offset+limit is collected to report hasMore
        // without scanning the rest of the model (capacity stays bound to the
        // page, not to the offset).
        var elements = new List<Element>(limit + 2);
        var matched = 0;
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();
            if (matched >= offset + limit + 1)
                break;

            if (categories is not null)
            {
                var categoryId = element.Category?.Id.GetValue();
                if (categoryId is null || !categories.Contains(categoryId.Value))
                    continue;
            }

            if (familyFilter is not null)
            {
                if (element is not FamilyInstance familyInstance
                    || familyInstance.Symbol?.Family is null
                    || !familyFilter.Contains(familyInstance.Symbol.Family.Id.GetValue()))
                {
                    continue;
                }
            }

            if (levelFilter.Count > 0)
            {
                var levelId = element.LevelId;
                if (levelId == ElementId.InvalidElementId || !levelFilter.Contains(levelId.GetValue()))
                    continue;
            }

            if (typeFilter.Count > 0)
            {
                var typeId = element.GetTypeId();
                if (typeId == ElementId.InvalidElementId || !typeFilter.Contains(typeId.GetValue()))
                    continue;
            }

            if (matched >= offset)
                elements.Add(element);
            matched++;
        }

        // The +1 probe element (if present) only proves there is more to page.
        var hasMore = elements.Count > limit;
        if (hasMore)
            elements.RemoveAt(elements.Count - 1);

        outcome.Description = "Remapped parameter values on " + elements.Count +
                              " element(s) (offset " + offset + ", limit " + limit + ")";
        outcome.Data["offset"] = offset;
        if (hasMore)
        {
            outcome.Data["hasMore"] = true;
            outcome.Data["nextOffset"] = offset + elements.Count;
            outcome.Warnings.Add(
                "More matching elements remain past this page — repeat the call with offset " +
                (offset + elements.Count) + " until hasMore is gone.");
        }

        var failedElements = 0;
        var resolutionFailedElements = 0;
        foreach (var element in elements)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport
            {
                Id = element.Id.GetValue(),
                Name = element.Category?.Name
            };

            var anyApplied = false;
            string? resolutionFailure = null;
            foreach (var pair in mapping)
            {
                var change = ApplyPair(
                    doc, element, pair, outcome,
                    out var parameterName, out var owner, out var pairFailure);
                resolutionFailure ??= pairFailure;
                if (change is null)
                    continue; // skipped with a reason recorded by ApplyPair

                anyApplied = true;
                report.Detail = (report.Detail is null ? string.Empty : report.Detail + "; ") +
                                parameterName;
                report.OldValue = (report.OldValue is null ? string.Empty : report.OldValue + " | ") +
                                  change.Value.OldValue;
                report.NewValue = (report.NewValue is null ? string.Empty : report.NewValue + " | ") +
                                  change.Value.NewValue;
                outcome.AffectedCount++;

                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = owner.UniqueId,
                    ElementId = owner.Id.GetValue(),
                    ParameterName = parameterName,
                    OldValue = change.Value.OldValue,
                    NewValue = change.Value.NewValue,
                    Status = ChangeStatus.Applied
                });
            }

            if (anyApplied)
                outcome.Batch.Changed.Add(report);
            else if (resolutionFailure is not null)
            {
                // A typo in a parameter name/scope is not "nothing to copy": every
                // element was skipped for a broken mapping, and reporting success
                // with noChanges let the model announce a migration that never ran.
                failedElements++;
                resolutionFailedElements++;
                report.ErrorCode = resolutionFailure;
                report.Reason = "No mapping pair could be applied: the source or target " +
                                "parameter did not resolve for this element.";
                outcome.Batch.Failed.Add(report);
            }
            else
            {
                failedElements++;
                report.Reason = "No mapping pair could be applied to this element.";
                outcome.Batch.Skipped.Add(report);
            }
        }

        if (atomic && failedElements > 0)
        {
            outcome.Aborted = true;
            outcome.Success = false;
            outcome.ErrorCode = "atomic_aborted";
            outcome.Message = failedElements + " element(s) could not be remapped and " +
                              "atomic=true, so the whole operation was rolled back. " +
                              "Nothing was changed.";
            return;
        }

        if (resolutionFailedElements > 0 && outcome.AffectedCount == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "parameter_not_found";
            outcome.Message =
                "None of the " + resolutionFailedElements + " element(s) could be remapped: " +
                "the source or target parameter did not resolve (see failed[]). " +
                "[Action: check the parameter name and scope; a type parameter needs scope=type]";
        }
    }

    private static (string OldValue, string NewValue)? ApplyPair(
        Document doc,
        Element element,
        RemapMapping pair,
        WriteOutcome outcome,
        out string parameterName,
        out Element owner,
        out string? resolutionFailure)
    {
        parameterName = string.Empty;
        owner = element;
        resolutionFailure = null;

        if (pair.Target is null)
            return null;

        var targetResolution = AgentParameterWriteHelper.Resolve(
            doc, element, pair.Target.Name, pair.Target.Guid, pair.Target.Scope);
        if (!targetResolution.Ok)
        {
            outcome.Warnings.Add(
                "Element " + element.Id.GetValue() + ": " + targetResolution.Reason);
            resolutionFailure = targetResolution.ErrorCode ?? "parameter_not_found";
            return null;
        }

        var target = targetResolution.Parameter!;
        parameterName = (targetResolution.IsTypeScope ? "type:" : "instance:") +
                        (target.Definition?.Name ?? pair.Target.Name ?? pair.Target.Guid ?? "?");
        if (targetResolution.IsTypeScope && target.Element is not null)
            owner = target.Element;

        if (pair.Source is null)
        {
            outcome.Warnings.Add(
                "Element " + element.Id.GetValue() + ": source parameter is not specified.");
            return null;
        }

        var sourceResolution = AgentParameterWriteHelper.Resolve(
            doc, element, pair.Source.Name, pair.Source.Guid, pair.Source.Scope);
        if (!sourceResolution.Ok)
        {
            outcome.Warnings.Add(
                "Element " + element.Id.GetValue() + ": " + sourceResolution.Reason);
            resolutionFailure = sourceResolution.ErrorCode ?? "parameter_not_found";
            return null;
        }

        var source = sourceResolution.Parameter!;
        var sourceValue = AgentParameterWriteHelper.Describe(source);
        if (string.IsNullOrEmpty(sourceValue))
            return null;

        if (!AgentParameterWriteHelper.TryConvertValue(
                source, target, null, out var text, out var conversionReason))
        {
            outcome.Warnings.Add("Element " + element.Id.GetValue() + ": " + conversionReason);
            return null;
        }

        var before = AgentParameterWriteHelper.RawValue(target);
        // No-op detection compares against the INTENDED value: reading the
        // target right after Set (no Regenerate inside the loop) returns stale
        // data for family parameters of placed instances, so a real copy used
        // to collapse into "skipped, 0 changes" while the value DID land
        // (v14 pilot — the instance-value migration path).
        if (string.Equals(before, text, StringComparison.Ordinal))
            return null;

        if (!AgentParameterWriteHelper.TrySet(target, text, "raw", out var errorCode, out var reason))
        {
            outcome.Warnings.Add("Element " + element.Id.GetValue() + ": " + errorCode + " — " + reason);
            return null;
        }

        return (before, text);
    }

    private static bool TryResolveCategories(
        Document doc,
        IReadOnlyList<string>? names,
        out HashSet<long>? ids,
        out string? errorCode,
        out string? reason)
    {
        ids = null;
        errorCode = null;
        reason = null;

        if (names is null || names.Count == 0)
            return true;

        var resolved = new HashSet<long>();
        var unknown = new List<string>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var name = raw.Trim();
            if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
            {
                var category = Category.GetCategory(doc, builtIn);
                if (category is not null)
                {
                    resolved.Add(category.Id.GetValue());
                    continue;
                }
            }

            var byDisplayName = FindCategoryByName(doc, name);
            if (byDisplayName is not null)
            {
                resolved.Add(byDisplayName.Id.GetValue());
                continue;
            }

            unknown.Add(name);
        }

        if (unknown.Count > 0)
        {
            errorCode = "unknown_category";
            reason = "Unknown category name(s): " + string.Join(", ", unknown) +
                     AgentCategoryResolver.SuggestSuffix(doc, unknown[0]) +
                     ". Pass a BuiltInCategory token (OST_Pipes) or the exact category name " +
                     "shown in the project.";
            return false;
        }

        ids = resolved.Count > 0 ? resolved : null;
        return true;
    }

    private static Category? FindCategoryByName(Document doc, string name)
    {
        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
                return category;
        }

        return null;
    }

    /// <summary>
    /// Resolves filter.familyIds/familyNames into a set of Family element ids.
    /// Every name/id must resolve (the batch contract used elsewhere too):
    /// a typo would silently remap nothing on a category-wide sweep.
    /// </summary>
    private static bool TryResolveFamilyFilter(
        Document doc,
        RemapFilter filter,
        out HashSet<long>? ids,
        out string? errorCode,
        out string? reason)
    {
        ids = null;
        errorCode = null;
        reason = null;

        var resolved = new HashSet<long>();
        var unresolved = new List<string>();

        foreach (var rawId in filter.FamilyIds ?? new List<long>())
        {
            if (doc.GetElement(ElementIdCompat.Create(rawId)) is Autodesk.Revit.DB.Family family)
            {
                resolved.Add(rawId);
                continue;
            }

            unresolved.Add("id " + rawId);
        }

        foreach (var rawName in filter.FamilyNames ?? new List<string>())
        {
            var name = rawName.Trim();
            if (name.Length == 0)
                continue;

            var family = AgentFamilyReader.ResolveFamily(
                doc, null, name, null, out var nameError, out var nameMessage);
            if (family is not null)
            {
                resolved.Add(family.Id.GetValue());
                continue;
            }

            unresolved.Add("'" + name + "' (" + (nameMessage ?? nameError ?? "not found") + ")");
        }

        if (unresolved.Count > 0)
        {
            errorCode = "family_not_found";
            reason = "filter.familyIds/familyNames must all resolve; unresolved: " +
                     string.Join(", ", unresolved) +
                     ". [Action: check the exact names with list_families]";
            return false;
        }

        if (resolved.Count > 0)
            ids = resolved;
        return true;
    }

    private sealed class RemapRequest
    {
        public List<RemapMapping>? Mapping { get; set; }

        public RemapFilter? Filter { get; set; }
    }
}
