using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed partial class FindFamiliesWithParameterTool
{
    /// <summary>
    /// Verification caps: one family document open (EditFamily) per candidate,
    /// so the default read stays bounded even on a several-hundred-family
    /// audit (the v15 live run: every family of a bound category matched a
    /// project binding, 200+ candidates).
    /// </summary>
    private const int DefaultVerificationLimit = 25;

    private const int MaxVerificationLimit = 200;

    /// <summary>
    /// Time budget of the verification pass (v16 D2): heavy vendor .rfa take
    /// ~1 s per EditFamily open, so a count cap alone hit the tool timeout at
    /// ~45 documents. The pass stops at the deadline and reports an honest
    /// nextVerificationOffset; the card timeout is 180 s, so the budget stays
    /// below it (the preliminary scan needs the rest — validator v16, P2-2).
    /// </summary>
    private const int DefaultVerificationSeconds = 120;

    private const int MaxVerificationSeconds = 150;

    private const int MaxScopeVerifyWarnings = 5;

    private static ScanOutcome ScanFamilies(
        Document doc,
        string parameterName,
        MatchMode mode,
        string? valueContains,
        HashSet<long>? categoryIds,
        bool includeInstanceParameters,
        bool summary,
        int maxMatchedTypes,
        bool requireShared,
        CancellationToken ct)
    {
        var outcome = new ScanOutcome();
        // The resolver is cheap after the first hit (definitionId cache) and the
        // binding index is one BindingMap pass: both are needed for isShared and
        // the family|project scope on EVERY match, not only in summary mode.
        var resolver = new AgentSharedParameterResolver(doc);
        var bindingIndex = new ProjectBindingIndex(doc);

        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilySymbol symbol)
                continue;

            outcome.TypesScanned++;
            var family = symbol.Family;
            if (family is null)
                continue;

            outcome.FamiliesSeen.Add(family.Id.GetValue());
            if (!MatchesCategory(family, categoryIds))
                continue;

            var scan = GetOrCreate(outcome.Families, family);
            scan.IsSharedFamily ??= family.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1;
            var seenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in symbol.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                var guid = resolver.ResolveGuid(parameter);
                AddShared(scan, guid, name!);

                if (!MatchesName(name!, parameterName, mode))
                    continue;

                if (requireShared && guid is null)
                    continue;

                // The scope filter is applied AFTER verification (see
                // ApplyScopeFilter): an assumed "family+project" used to pass
                // the family+ filter and later turn out to be a project-only
                // binding, silently poisoning migration plans.
                var parameterScope = bindingIndex.ScopeOf(name!, guid, family.FamilyCategory);

                var value = SafeValueString(parameter);
                if (!string.IsNullOrWhiteSpace(valueContains)
                    && !AgentFamilySpecs.ContainsNormalized(value, valueContains!))
                {
                    continue;
                }

                if (!seenParameterNames.Add(name!))
                    continue;

                scan.MergeScope(name!, parameterScope, guid, typeMatch: true);
                outcome.AggregateName(scan.FamilyId, name!, guid);

                // The cap belongs to the family, not to one type: a family with
                // 40 matching types used to return 40 rows (external run 6, P2).
                if (scan.TypeMatches.Count >= maxMatchedTypes)
                    continue;

                scan.TypeMatches.Add(new Dictionary<string, object?>
                {
                    ["scope"] = "type",
                    ["typeId"] = symbol.Id.GetValue(),
                    ["typeName"] = symbol.Name,
                    ["parameterName"] = name,
                    ["value"] = value,
                    ["parameterScope"] = parameterScope,
                    ["isShared"] = guid is not null,
                    ["guid"] = guid
                });
            }
        }

        if (includeInstanceParameters)
        {
            ScanPlacedInstances(
                doc,
                categoryIds,
                parameterName,
                mode,
                valueContains,
                maxMatchedTypes,
                resolver,
                bindingIndex,
                requireShared,
                outcome,
                ct);
        }
        else
        {
            // Direct/nested instance counts stay available WITHOUT the heavy
            // parameter scan: the migration plan needs to see nested-only SHARED
            // families (flanges, bolts — their instances live inside host
            // families, so a "placed" filter or an eyeballed family list misses
            // them), and one reload of such a family updates every host.
            CollectInstanceBreakdown(doc, categoryIds, outcome, ct);
        }

        return outcome;
    }

    /// <summary>
    /// One light collector pass classifying each placed instance as direct
    /// (SuperComponent null) or nested inside a host instance — without reading
    /// any parameter. The per-instance parameter scan does the same counts when
    /// <c>includeInstanceParameters</c> is on.
    /// </summary>
    private static void CollectInstanceBreakdown(
        Document doc,
        HashSet<long>? categoryIds,
        ScanOutcome outcome,
        CancellationToken ct)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilyInstance instance)
                continue;

            var family = instance.Symbol?.Family;
            if (family is null)
                continue;

            outcome.FamiliesSeen.Add(family.Id.GetValue());
            if (!MatchesCategory(family, categoryIds))
                continue;

            var scan = GetOrCreate(outcome.Families, family);
            if (instance.SuperComponent is null)
                scan.DirectInstanceCount++;
            else
                scan.NestedInstanceCount++;
        }
    }

    private static void ScanPlacedInstances(
        Document doc,
        HashSet<long>? categoryIds,
        string parameterName,
        MatchMode mode,
        string? valueContains,
        int maxMatchedTypes,
        AgentSharedParameterResolver resolver,
        ProjectBindingIndex bindingIndex,
        bool requireShared,
        ScanOutcome outcome,
        CancellationToken ct)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilyInstance instance)
                continue;

            outcome.InstancesScanned++;
            var family = instance.Symbol?.Family;
            if (family is null)
                continue;

            outcome.FamiliesSeen.Add(family.Id.GetValue());
            if (!MatchesCategory(family, categoryIds))
                continue;

            var scan = GetOrCreate(outcome.Families, family);
            scan.PlacedInstanceCount++;
            if (instance.SuperComponent is null)
                scan.DirectInstanceCount++;
            else
                scan.NestedInstanceCount++;

            var seenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in instance.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                var guid = resolver.ResolveGuid(parameter);
                AddShared(scan, guid, name!);

                if (!MatchesName(name!, parameterName, mode))
                    continue;

                if (requireShared && guid is null)
                    continue;

                var parameterScope = bindingIndex.ScopeOf(name!, guid, family.FamilyCategory);

                var value = SafeValueString(parameter);
                if (!string.IsNullOrWhiteSpace(valueContains)
                    && !AgentFamilySpecs.ContainsNormalized(value, valueContains!))
                {
                    continue;
                }

                if (!seenParameterNames.Add(name!))
                    continue;

                if (!scan.InstanceParameters.TryGetValue(name!, out var match))
                {
                    match = new InstanceParameterMatch
                    {
                        ParameterName = name!,
                        FirstValue = value,
                        Guid = guid,
                        ParameterScope = parameterScope
                    };
                    scan.InstanceParameters.Add(name!, match);
                    scan.MergeScope(name!, parameterScope, guid, typeMatch: false);
                    outcome.AggregateName(scan.FamilyId, name!, guid);

                    if (scan.InstanceMatches.Count < maxMatchedTypes)
                        scan.InstanceMatches.Add(match.ToRow());
                }

                match.AddInstance(instance.Id.GetValue(), value);
                if (match.Row is not null)
                    match.SyncRow();
            }
        }
    }

    /// <summary>
    /// Opens the family document of every matched family whose assumed scope
    /// says "project-bound" and reclassifies each matched name against the
    /// real <see cref="FamilyManager"/> parameter list: present = family (or
    /// family+project when the definition is also bound in the project),
    /// absent = project (the binding alone produced the match — the vendor-prefix
    /// lesson of the v15 run: 9 project bindings made every bound-category
    /// family look "already ported"). Bounded by the verification limit; the
    /// remaining candidates are marked "unverified" and their scopes stay
    /// assumptions.
    /// </summary>
    private static void VerifyFamilyScopes(
        Document doc,
        ScanOutcome outcome,
        int verificationLimit,
        int verificationOffset,
        int verificationSeconds,
        List<string> warnings,
        CancellationToken ct)
    {
        var candidates = outcome.Families.Values
            .Where(item => item.HasMatches && item.ScopeByName.Values.Any(IsAssumedProjectBound))
            .OrderBy(item => item.FamilyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var verified = 0;
        var failed = 0;
        var skipped = 0;
        var truncated = false;
        // Time budget: opening family documents dominates the cost (~1 s each
        // on heavy vendor .rfa), so a count cap alone either timed the tool out
        // or left the model half-verified. The deadline stops the pass EARLY
        // with an honest nextVerificationOffset instead of a tool_timeout
        // (v16 live run, D2: 45 documents already hit the 60 s cap).
        var deadline = verificationSeconds > 0
            ? DateTime.UtcNow.AddSeconds(verificationSeconds)
            : (DateTime?)null;
        foreach (var scan in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (skipped < verificationOffset)
            {
                skipped++;
                scan.ScopeVerification = "unverified";
                continue;
            }

            if (verified >= verificationLimit
                || (deadline is not null && DateTime.UtcNow >= deadline
                    && verified + failed > 0))
            {
                truncated = true;
                scan.ScopeVerification = "unverified";
                continue;
            }

            if (doc.GetElement(ElementIdCompat.Create(scan.FamilyId))
                is not Autodesk.Revit.DB.Family family)
            {
                scan.ScopeVerification = "failed";
                scan.ScopeVerifyError = "family_not_found";
                failed++;
                continue;
            }

            if (!AgentFamilyReader.TryOpenFamilyDocument(
                    doc, family, out var familyDoc, out var errorCode, out var message))
            {
                scan.ScopeVerification = "failed";
                scan.ScopeVerifyError = errorCode;
                failed++;
                if (warnings.Count < MaxScopeVerifyWarnings)
                {
                    warnings.Add("Scope verification failed for family '" + scan.FamilyName +
                                 "': " + message);
                }

                continue;
            }

            try
            {
                var namesInFamily = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var namesByGuid = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (FamilyParameter parameter in familyDoc!.FamilyManager.Parameters)
                {
                    var parameterName = parameter.Definition?.Name;
                    if (string.IsNullOrEmpty(parameterName))
                        continue;

                    namesInFamily[parameterName!] = parameter.IsInstance;
                    // get_GUID() throws for non-shared family parameters: only
                    // a shared one carries the GUID to index by.
                    try
                    {
                        var familyGuid = parameter.GUID;
                        if (familyGuid != Guid.Empty)
                            namesByGuid[familyGuid.ToString("D")] = parameterName!;
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                    {
                        // Not a shared parameter: the name index above covers it.
                    }
                }

                foreach (var name in scan.ScopeByName.Keys.ToList())
                {
                    var guid = scan.GuidByName.TryGetValue(name, out var known) ? known : null;
                    string? presentName = null;
                    var presentIsInstance = false;
                    if (guid is not null && namesByGuid.TryGetValue(guid, out var byGuid))
                    {
                        presentName = byGuid;
                        presentIsInstance = namesInFamily[byGuid];
                    }
                    else if (namesInFamily.TryGetValue(name, out var byName))
                    {
                        presentName = name;
                        presentIsInstance = byName;
                    }

                    var wasBound = IsAssumedProjectBound(scan.ScopeByName[name]);
                    var finalScope = presentName is null
                        ? "project"
                        : wasBound ? "family+project" : "family";
                    scan.ApplyVerifiedScope(
                        name, finalScope, presentName is not null,
                        presentName is null ? null : presentIsInstance);
                }

                scan.ScopeVerification = "verified";
                verified++;
            }
            finally
            {
                try
                {
                    familyDoc!.Close(false);
                }
                catch (Exception)
                {
                    // The read-only copy is discarded either way.
                }
            }
        }

        outcome.ScopeVerificationStats = new ScopeVerificationStats
        {
            Candidates = candidates.Count,
            Verified = verified,
            Failed = failed,
            SkippedByOffset = skipped,
            Unverified = candidates.Count - verified - failed - skipped,
            Truncated = truncated,
            Limit = verificationLimit,
            Offset = verificationOffset
        };

        // An offset beyond the candidate list verifies nothing and used to
        // look like "verification complete" without any hint (validator v16, P3).
        if (verificationOffset > 0 && verificationOffset >= candidates.Count)
        {
            warnings.Add(
                "verificationOffset " + verificationOffset + " is at or beyond the " +
                candidates.Count + " verification candidate(s): nothing was verified in " +
                "this pass. [Action: use the value from scopeVerification.nextVerificationOffset " +
                "of the previous call]");
        }
    }

    /// <summary>
    /// The parameterScope filter runs on the FINAL scopes (after verification):
    /// a name that verified as "project" is dropped from family+ results
    /// instead of surviving on its pre-verification assumption.
    /// </summary>
    private static void ApplyScopeFilter(ScanOutcome outcome, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)
            || string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var scan in outcome.Families.Values)
        {
            var dropped = scan.ScopeByName
                .Where(pair => !MatchesParameterScope(pair.Value, filter))
                .Select(pair => pair.Key)
                .ToList();
            if (dropped.Count == 0)
                continue;

            foreach (var name in dropped)
            {
                scan.ScopeByName.Remove(name);
                scan.GuidByName.Remove(name);
                scan.InstanceParameters.Remove(name);
                // The per-name type counter feeds MatchedTypesTotal: leaving a
                // dropped name here made matchedTypeCount contradict the rows
                // and the per-family matchedParameterCount (validator P1, v15).
                scan.TypeMatchesByName.Remove(name);
            }

            scan.TypeMatches.RemoveAll(row =>
                dropped.Contains((string)row["parameterName"]!, StringComparer.OrdinalIgnoreCase));
            scan.InstanceMatches.RemoveAll(row =>
                dropped.Contains((string)row["parameterName"]!, StringComparer.OrdinalIgnoreCase));
        }

        foreach (var aggregate in outcome.ByParameterName.Values.ToList())
        {
            aggregate.Families.RemoveWhere(id =>
                !outcome.Families.TryGetValue(id, out var scan)
                || !scan.ScopeByName.ContainsKey(aggregate.Name));
            if (aggregate.Families.Count == 0)
                outcome.ByParameterName.Remove(aggregate.Name);
        }
    }

    private static bool IsAssumedProjectBound(string scope) =>
        string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, "family+project", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesParameterScope(string parameterScope, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)
            || string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "family+" is the migration-planning filter: family OR family+project —
        // everything that actually lives in family documents (the live run showed
        // half of the vendor-prefixed names are family+project; a strict "family"
        // filter silently dropped them).
        if (string.Equals(filter, "family+", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(parameterScope, "family", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(parameterScope, "family+project", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(parameterScope, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddShared(
        FamilyScan scan,
        string? guid,
        string name)
    {
        if (guid is null)
            return;

        scan.SharedGuids.Add(guid);
        scan.SharedParameterNames.Add(name);
    }

    private static FamilyScan GetOrCreate(
        Dictionary<long, FamilyScan> families,
        Autodesk.Revit.DB.Family family)
    {
        var id = family.Id.GetValue();
        if (families.TryGetValue(id, out var existing))
            return existing;

        var created = new FamilyScan
        {
            FamilyId = id,
            FamilyUniqueId = family.UniqueId,
            FamilyName = family.Name,
            Category = family.FamilyCategory?.Name
        };
        families.Add(id, created);
        return created;
    }

    private static bool MatchesCategory(
        Autodesk.Revit.DB.Family family,
        HashSet<long>? categoryIds) =>
        categoryIds is null
        || (family.FamilyCategory is not null
            && categoryIds.Contains(family.FamilyCategory.Id.GetValue()));

    private static bool MatchesName(string name, string parameterName, MatchMode mode) =>
        mode switch
        {
            MatchMode.Contains => AgentFamilySpecs.ContainsNormalized(name, parameterName),
            MatchMode.Prefix => AgentFamilyText.NormalizeForMatch(name).StartsWith(
                AgentFamilyText.NormalizeForMatch(parameterName),
                StringComparison.Ordinal),
            _ => string.Equals(name, parameterName.Trim(), StringComparison.OrdinalIgnoreCase)
        };

    private static string? SafeValueString(Parameter parameter)
    {
        try
        {
            return parameter.AsValueString() ?? parameter.AsString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One pass over doc.ParameterBindings: shared GUIDs (and non-shared names)
    /// bound at the PROJECT level. A parameter that is only bound here does not
    /// exist in the family document (the vendor-prefix lesson of the ФОП session:
    /// 194 "families" were project bindings, family docs held zero).
    /// </summary>
}
