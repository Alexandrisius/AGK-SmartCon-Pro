using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Reads the parameters of a family document with type scope: definition
/// metadata (instance/type, shared GUID, storage, formula, lookup tables,
/// canSetValue/canSetFormula) and values per family type. The batch form
/// (<c>familyIds</c>, up to 50) reads several families in one call - the
/// benchmark kept losing rounds to one-by-one calls over family lists.
/// </summary>
public sealed class GetFamilyParametersTool : AgentReadToolBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;
    private const int DefaultMaxValues = 20;
    private const int MaxValues = 100;
    private const int MaxNestedFamiliesReported = 50;
    private const int LargeResultHintChars = 20000;

    /// <summary>
    /// Batch size the compact rows aim at: the orchestrator result budget minus
    /// room for the envelope and the message.
    /// </summary>
    private const int BatchTargetCharacters =
        ToolResultBudget.DefaultMaxCharacters - 800;

    private const int MinShownParameters = 5;

    public GetFamilyParametersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_family_parameters",
        Description =
            "Lists family parameters with their scope (instance/type), id (FamilyParameter.Id), " +
            "shared GUID, isBuiltIn (built-in parameters cannot be removed), storage type, " +
            "formula, lookup tables, canSetValue/canSetFormula and the value of each family type. " +
            "parametersTotalInFamily is the count BEFORE nameContains/scope filters; matchedCount is " +
            "the number of parameters that matched the filters and total is its paging total " +
            "(matchedCount=0 means the filter found nothing; the family was still read). " +
            "parameters[] is flat: address a row with " +
            "parameters.byId[id] or parameters.byName[name], a type value with " +
            "valuesByType.byName[typeName]. " +
            "familyIds (array, up to 50) reads several families in ONE call and returns " +
            "families[] with the same per-family payload (nameContains/scope/limits apply " +
            "to every family); a batch that exceeds the context budget keeps EVERY family " +
            "as compact rows (name, guid, storage, instance flag, counts; compact=true) " +
            "instead of dropping families - formulas and values then need the single-family " +
            "form. compact=true asks for those compact rows explicitly. " +
            "parameterNamePrefix filters every family's parameter list to names with that " +
            "prefix (case-insensitive) — the cheap one-call ФОП audit ('which family " +
            "documents actually embed the definitions') without paging unfiltered " +
            "parameters. " +
            "isShared/nestedFamilies[] report the family's own shared flag and its nested " +
            "subfamilies with theirs: a SHARED nested family is scheduled as a separate row " +
            "and its parameters must be shared too, while a geometric nesting needs nothing " +
            "(nestedCount/sharedNestedCount are the audit numbers). " +
            "Opens the family document in the background; the Family Editor is never shown.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""familyId"":{""type"":""integer""},""familyIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":50,""description"":""Batch: read several families in one call; each entry gets the same payload as the single-family form. In a batch includeValues defaults to FALSE (metadata only — values are the timeout hot spot); pass includeValues=true explicitly to read per-type values""},""compact"":{""type"":""boolean"",""default"":false,""description"":""Return compact parameter rows (name, guid, storage, instance flag, counts) without formulas/values — the ФОП-audit shape a large batch falls back to anyway""},""parameterNamePrefix"":{""type"":""string"",""description"":""Keep only parameters whose name starts with this prefix (case-insensitive) — e.g. a vendor or ФОП prefix; turns a 50-family batch into a one-call audit of which family documents embed these definitions""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""nameContains"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""all"",""instance"",""type""],""default"":""all""},""includeValues"":{""type"":""boolean"",""description"":""Per-type values: true for a single family (default), false for a batch (default) — one parameter's values: get_family_parameter_values""},""maxValuesPerParameter"":{""type"":""integer"",""default"":20,""maximum"":100},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        // Batch reads open family documents: dismiss and report modal dialogs
        // instead of hanging on a click (live v17, P0 1.1).
        using var dialogs = AgentFamilyDialogSuppressor.BeginSafe(Context);
        var batchIds = TryGetIntArray(args, "familyIds");
        if (batchIds is { Count: > 0 })
            return ExecuteBatch(doc, args, batchIds, dialogs, ct);

        var family = AgentFamilyReader.ResolveFamily(
            doc,
            TryGetLong(args, "familyId"),
            AgentJson.GetString(args, "familyName"),
            AgentJson.GetString(args, "familyUniqueId"),
            out var errorCode,
            out var message);
        if (family is null)
            return ToolResult.Fail(errorCode ?? "family_not_found", message ?? "Family not found.");

        var (payload, familyMessage, failure) = ReadFamily(
            doc, args, family, AgentJson.GetBool(args, "includeValues", true), ct);
        if (payload is null)
            return ToolResult.Fail(
                failure ?? "family_open_failed", familyMessage);

        AttachSuppressedDialogs(payload, dialogs);

        if (AgentJson.GetBool(args, "compact", false))
        {
            // CompactBatch builds short rows only for payloads marked ok
            // (the batch path sets it while collecting); without the flag the
            // single-family compact reply was the FULL payload with a
            // "compact":true envelope (validator v16, P1-2).
            payload["ok"] = true;
            return CompactBatch(
                new List<object?> { payload }, 1, 1, 0,
                "Family '" + family.Name + "'");
        }

        var json = AgentJson.Serialize(payload);

        // One gentle nudge when the single-family result is large: the model
        // that keeps calling this per family should switch to the batch form.
        if (json.GetRawText().Length > LargeResultHintChars
            && familyMessage.IndexOf("familyIds", StringComparison.Ordinal) < 0)
        {
            familyMessage += " NOTE: reading several families? Pass familyIds (up to 50) " +
                             "to read them all in one call.";
        }

        return ToolResult.Ok(json, familyMessage);
    }

    private ToolResult ExecuteBatch(
        Document doc,
        JsonElement args,
        IReadOnlyList<long> familyIds,
        AgentFamilyDialogSuppressor? dialogs,
        CancellationToken ct)
    {
        var families = new List<object?>(familyIds.Count);
        var okCount = 0;
        // Batch reads default to metadata-only: values per type dominate the
        // runtime (a 4-family batch with values blew the 30 s tool timeout twice
        // in the ФОП session; 20 families without values took 6.4 s). An explicit
        // includeValues=true still reads them.
        var batchIncludeValues = AgentJson.GetBool(args, "includeValues", false);
        foreach (var id in familyIds)
        {
            ct.ThrowIfCancellationRequested();

            var startedAt = DateTime.UtcNow;
            var family = AgentFamilyReader.ResolveFamily(
                doc, id, null, null, out var errorCode, out var message);
            if (family is null)
            {
                families.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = id,
                    ["ok"] = false,
                    ["error"] = errorCode ?? "family_not_found",
                    ["message"] = message ?? "Family not found."
                });
                continue;
            }

            var (payload, familyMessage, failure) = ReadFamily(doc, args, family, batchIncludeValues, ct);
            if (payload is null)
            {
                families.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = id,
                    ["ok"] = false,
                    ["error"] = failure ?? "family_open_failed",
                    ["message"] = familyMessage,
                    ["elapsedMs"] = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds
                });
                continue;
            }

            payload["ok"] = true;
            payload["elapsedMs"] = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            families.Add(payload);
            okCount++;
        }

        var failedCount = familyIds.Count - okCount;
        var batchPayload = new Dictionary<string, object?>
        {
            ["families"] = families,
            ["familyCount"] = familyIds.Count,
            ["okCount"] = okCount,
            ["failedCount"] = failedCount,
            ["includeValues"] = batchIncludeValues
        };
        AttachSuppressedDialogs(batchPayload, dialogs);

        var baseMessage = "Read " + okCount + " of " + familyIds.Count + " family(ies)" +
                          (failedCount > 0 ? ", " + failedCount + " not found" : string.Empty) +
                          (batchIncludeValues
                              ? string.Empty
                              : " (values off by default in a batch — pass includeValues=true " +
                                "to read per-type values, or use get_family_parameter_values " +
                                "for one parameter)");

        // Live case 2 round 4: a 20-family batch exceeded the context budget and
        // the generic trimmer halved the families array - the model re-read the
        // same ids. When the full batch does not fit, every family is kept as a
        // COMPACT row (parameter name, guid, storage, instance flag, counts);
        // formulas and values stay one single-family call away.
        if (!AgentJson.GetBool(args, "compact", false)
            && AgentJson.Serialize(batchPayload).GetRawText().Length <= BatchTargetCharacters)
        {
            return ToolResult.Ok(
                AgentJson.Serialize(batchPayload), baseMessage + ".");
        }

        return CompactBatch(families, familyIds.Count, okCount, failedCount, baseMessage);
    }

    private static void AttachSuppressedDialogs(
        Dictionary<string, object?> payload,
        AgentFamilyDialogSuppressor? dialogs)
    {
        if (dialogs?.Suppressed.Count is not > 0)
            return;

        payload["suppressedDialogs"] = dialogs.Suppressed
            .Select(dialog => new Dictionary<string, object?>
            {
                ["dialogId"] = dialog.DialogId,
                ["message"] = dialog.Message,
                ["answer"] = dialog.Answer
            })
            .ToList();
    }

    private ToolResult CompactBatch(
        List<object?> families,
        int familyCount,
        int okCount,
        int failedCount,
        string baseMessage)
    {
        var shown = int.MaxValue;
        Dictionary<string, object?> payload;
        while (true)
        {
            payload = new Dictionary<string, object?>
            {
                ["compact"] = true,
                ["families"] = BuildCompactRows(families, shown),
                ["familyCount"] = familyCount,
                ["okCount"] = okCount,
                ["failedCount"] = failedCount,
                ["hint"] = "Compact rows: parameter name, guid, storage, spec, group, " +
                           "instance flag, type count. For formulas, values and lookup " +
                           "tables call get_family_parameters with a single familyId."
            };
            if (AgentJson.Serialize(payload).GetRawText().Length <= BatchTargetCharacters
                || shown <= MinShownParameters)
            {
                break;
            }

            shown = Math.Max(MinShownParameters, shown / 2);
        }

        var stillLarge = AgentJson.Serialize(payload).GetRawText().Length > BatchTargetCharacters;
        var shownCount = CountShownParameters(payload);
        var messageText = baseMessage + ". COMPACT rows for every family: parameter name, " +
                          "guid, storage, instance flag" +
                          (shownCount.HasValue
                              ? ", " + shownCount.Value + " parameter(s) per family shown"
                              : string.Empty) +
                          "; formulas and values via a single-family call (familyId)" +
                          (stillLarge
                              ? "; the batch is still too large for the context - split " +
                                "familyIds into several calls"
                              : string.Empty) + ".";
        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private static List<object?> BuildCompactRows(List<object?> families, int shown)
    {
        var rows = new List<object?>(families.Count);
        foreach (var family in families)
        {
            if (family is not Dictionary<string, object?> payload
                || payload.GetValueOrDefault("ok") is not true)
            {
                rows.Add(family);
                continue;
            }

            var reports = payload.GetValueOrDefault("parameters")
                as List<FamilyParameterReport> ?? new List<FamilyParameterReport>();
            var row = new Dictionary<string, object?>
            {
                ["familyId"] = payload.GetValueOrDefault("familyId"),
                ["familyName"] = payload.GetValueOrDefault("familyName"),
                ["ok"] = true,
                ["typeCount"] = payload.GetValueOrDefault("typeCount"),
                ["isShared"] = payload.GetValueOrDefault("isShared"),
                ["nestedCount"] = payload.GetValueOrDefault("nestedCount"),
                ["sharedNestedCount"] = payload.GetValueOrDefault("sharedNestedCount"),
                ["parametersTotalInFamily"] = payload.GetValueOrDefault("parametersTotalInFamily"),
                ["matchedCount"] = payload.GetValueOrDefault("matchedCount"),
                ["nameContains"] = payload.GetValueOrDefault("nameContains"),
                ["total"] = payload.GetValueOrDefault("total"),
                ["parameters"] = reports
                    .Take(shown)
                    .Select(parameter => new Dictionary<string, object?>
                    {
                        ["name"] = parameter.Name,
                        ["guid"] = parameter.Guid,
                        ["storageType"] = parameter.StorageType,
                        // spec/group decide whether a ФОП definition is
                        // compatible BEFORE any write (live v17, 2.2).
                        ["spec"] = parameter.Spec,
                        ["group"] = parameter.Group,
                        ["isInstance"] = parameter.IsInstance
                    })
                    .ToList()
            };
            if (reports.Count > shown)
            {
                row["parametersShown"] = Math.Min(shown, reports.Count);
                row["parametersTruncated"] = true;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static int? CountShownParameters(Dictionary<string, object?> payload)
    {
        if (payload.GetValueOrDefault("families") is not List<object?> rows || rows.Count == 0)
            return null;

        foreach (var row in rows)
        {
            if (row is Dictionary<string, object?> family
                && family.GetValueOrDefault("ok") is true
                && family.TryGetValue("parametersShown", out _))
            {
                return family.GetValueOrDefault("parametersShown") as int?;
            }
        }

        return null;
    }

    private (Dictionary<string, object?>? Payload, string Message, string? ErrorCode) ReadFamily(
        Document doc,
        JsonElement args,
        Autodesk.Revit.DB.Family family,
        bool includeValues,
        CancellationToken ct)
    {
        var scopeText = AgentJson.GetString(args, "scope");
        bool? wantInstance = null;
        if (!string.IsNullOrWhiteSpace(scopeText)
            && !string.Equals(scopeText, "all", StringComparison.OrdinalIgnoreCase))
        {
            wantInstance = AgentFamilyReader.ParseScope(scopeText, out var scopeMessage);
            if (wantInstance is null)
                return (null, scopeMessage ?? "scope must be all, instance or type.",
                    "invalid_arguments");
        }

        var nameContains = AgentJson.GetString(args, "nameContains");
        // Prefix filter: the one-call ФОП audit shape (v16 C1) — keeps only
        // parameters whose name starts with the prefix, so a 50-family batch
        // answers "which documents embed these definitions" without unfiltered
        // parameter dumps.
        var namePrefix = AgentJson.GetString(args, "parameterNamePrefix")?.Trim();
        var maxValues = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "maxValuesPerParameter", DefaultMaxValues),
            DefaultMaxValues,
            MaxValues);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        // ФОП session: one family took 21.4 s with no visible reason; the timers
        // below make the open/report split visible in smartcon.log (Debug level).
        Document? familyDoc = null;
        string? openErrorCode = null;
        string? openMessage = null;
        bool opened;
        using (SmartConLogger.Measure("FamilyDocOpen"))
        {
            opened = AgentFamilyReader.TryOpenFamilyDocument(
                doc,
                family,
                out familyDoc,
                out openErrorCode,
                out openMessage);
        }

        if (!opened)
        {
            return (null,
                openMessage ?? "The family document could not be opened.",
                openErrorCode ?? "family_open_failed");
        }

        try
        {
            var manager = familyDoc!.FamilyManager;
            var types = AgentFamilyReader.GetTypes(manager);

            // Shared nested families are scheduled as separate rows and need
            // shared parameters of their own; geometric nesting needs nothing.
            // Collected from the already-open family document (v14 pilot).
            var nestedRows = new List<object>();
            var nestedTotal = 0;
            var sharedNestedCount = 0;
            var nestedTruncated = false;
            foreach (var element in new FilteredElementCollector(familyDoc)
                         .OfClass(typeof(Autodesk.Revit.DB.Family)))
            {
                if (element is not Autodesk.Revit.DB.Family nestedFamily || nestedFamily.IsOwnerFamily)
                    continue;

                nestedTotal++;
                var isShared = nestedFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)
                    ?.AsInteger() == 1;
                if (isShared)
                    sharedNestedCount++;
                if (nestedRows.Count < MaxNestedFamiliesReported)
                    nestedRows.Add(new { name = nestedFamily.Name, isShared });
                else
                    nestedTruncated = true;
            }

            var ownerIsShared = familyDoc.OwnerFamily
                .get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1;

            var filtered = new List<FamilyParameter>();
            foreach (var parameter in manager.GetParameters())
            {
                ct.ThrowIfCancellationRequested();

                if (wantInstance.HasValue && parameter.IsInstance != wantInstance.Value)
                    continue;

                if (!string.IsNullOrWhiteSpace(nameContains)
                    && !AgentFamilySpecs.ContainsNormalized(
                        parameter.Definition.Name,
                        nameContains!))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(namePrefix)
                    && !SmartCon.Core.Services.Agent.AgentFamilyText.NormalizeForMatch(parameter.Definition.Name)
                        .StartsWith(
                            SmartCon.Core.Services.Agent.AgentFamilyText.NormalizeForMatch(namePrefix!),
                            StringComparison.Ordinal))
                {
                    continue;
                }

                filtered.Add(parameter);
            }

            var page = AgentPaging.Slice(filtered, offset, limit);
            var reports = new List<FamilyParameterReport>(page.Count);
            using (var reportTimer = SmartConLogger.Measure("FamilyParameterReports"))
            {
                foreach (var parameter in page)
                {
                    ct.ThrowIfCancellationRequested();
                    reports.Add(AgentFamilyReader.BuildParameterReport(
                        parameter,
                        types,
                        includeValues,
                        maxValues,
                        familyDoc));
                }
            }

            var nextOffset = offset + reports.Count;
            var typeNames = new List<string>();
            for (var index = 0; index < types.Count && index < 100; index++)
                typeNames.Add(types[index].Name);

            var payload = new Dictionary<string, object?>
            {
                ["familyId"] = family.Id.GetValue(),
                ["familyName"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                ["isShared"] = ownerIsShared,
                ["nestedCount"] = nestedTotal,
                ["sharedNestedCount"] = sharedNestedCount,
                ["nestedFamilies"] = nestedRows,
                ["nestedFamiliesTruncated"] = nestedTruncated ? true : null,
                // Total in the family BEFORE nameContains/scope filters: "total" below
                // is the paging total of the filtered set and used to read as "the
                // family only has this many parameters" (external harness feedback).
                ["parametersTotalInFamily"] = manager.GetParameters().Count,
                ["matchedCount"] = filtered.Count,
                ["nameContains"] = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains,
                ["parameterNamePrefix"] = string.IsNullOrWhiteSpace(namePrefix) ? null : namePrefix,
                ["typeCount"] = types.Count,
                ["types"] = typeNames,
                ["typesTruncated"] = types.Count > typeNames.Count,
                ["currentType"] = manager.CurrentType?.Name,
                ["parameters"] = reports,
                ["total"] = filtered.Count,
                ["returned"] = reports.Count,
                ["offset"] = offset,
                ["nextOffset"] = nextOffset < filtered.Count ? nextOffset : (int?)null,
                ["truncated"] = nextOffset < filtered.Count,
                ["maxValuesPerParameter"] = maxValues
            };

            var message = "Family '" + family.Name + "': " + reports.Count + " of " +
                          filtered.Count + " matched parameter(s) of " +
                          manager.GetParameters().Count + " in the family, " + types.Count +
                          " type(s)" +
                          (nextOffset < filtered.Count ? ", next offset " + nextOffset : string.Empty) +
                          (nestedTotal > 0
                              ? "; " + nestedTotal + " nested famil(ies), " + sharedNestedCount +
                                " SHARED (scheduled separately)"
                              : string.Empty) +
                          (types.Count == 0 ? ". The family has no types (empty family)." : ".");
            return (payload, message, null);
        }
        finally
        {
            CloseQuietly(familyDoc);
        }
    }

    private static void CloseQuietly(Document? familyDoc)
    {
        // FamilyManager batch pattern (#96 / REVIT-237190): close + COM release +
        // render-thread nudge.
        AgentFamilyDocCloser.Close(familyDoc);
    }

    private static bool Has(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        if (args.TryGetProperty(name, out _))
            return true;

        foreach (var property in args.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static long? TryGetLong(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            return null;
        }

        return result;
    }

    private static IReadOnlyList<long>? TryGetIntArray(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ids = new List<long>();
        foreach (var item in value.EnumerateArray())
        {
            // Non-integer entries never reach the tool in production (schema
            // validation); a malformed direct call falls back to the single path.
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var id))
                return null;
            ids.Add(id);
        }

        return ids;
    }
}
