using Autodesk.Revit.DB;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Read-only plan builder of the replace action: for every replacement it finds
/// the source parameter, resolves the target shared definition in the active
/// shared parameter file, guards the known ReplaceParameter failure modes
/// (builtin source, same GUID, spec mismatch) and captures the per-type value
/// snapshot plus the formula blast radius. Nothing is written here.
/// </summary>
public sealed partial class ReplaceFamilyParameterJobAction
{
    private static FamilyReplacePlan? BuildPlan(
        Document projectDoc,
        RevitFamilyManager manager,
        IReadOnlyList<ReplaceFamilyReplacementSpec>? replacements,
        CancellationToken ct,
        out string? error)
    {
        error = null;
        if (replacements is null || replacements.Count == 0)
        {
            error = "replacements[] is required and must not be empty. [Action: add at least one replacement]";
            return null;
        }

        var plan = new FamilyReplacePlan();
        for (var index = 0; index < replacements.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var replacement = replacements[index];
            var label = "replacement " + (index + 1) + ": ";

            var sourceRef = replacement.Source!;
            var source = AgentFamilyReader.FindParameter(
                manager,
                sourceRef.Name,
                sourceRef.SharedParameterGuid,
                null,
                out var sourceCode,
                out var sourceMessage);
            if (source is null)
            {
                error = label + "source: " + (sourceMessage ?? "parameter not found (" + sourceCode + ")") +
                        " [Action: check the parameter name/guid with get_family_parameters]";
                return null;
            }

            var sourceName = source.Definition.Name;
            if (AgentFamilyReader.IsBuiltIn(source))
            {
                error = label + "the parameter '" + sourceName + "' is a built-in Revit parameter and cannot " +
                        "be replaced. [Action: pick a family or shared parameter]";
                return null;
            }

            var targetRef = replacement.Target!;
            var resolution = FamilySharedDefinitions.FindExisting(
                projectDoc,
                targetRef.Name,
                targetRef.SharedParameterGuid);
            if (!resolution.Ok)
            {
                error = label + "target: " + (resolution.Message ?? "definition not found") +
                        " [Action: pick an existing definition from list_shared_parameters]";
                return null;
            }

            var target = resolution.Definition!;
            var sourceGuid = SharedGuidOf(source);
            var alreadyOnTarget = sourceGuid.HasValue && sourceGuid.Value == target.GUID;

            if (!alreadyOnTarget && !AgentFamilySpecs.SameSpec(source.Definition, target))
            {
                error = label + "source '" + sourceName + "' has spec '" +
                        AgentFamilySpecs.SpecName(source.Definition) + "', the target definition '" + target.Name +
                        "' has spec '" + AgentFamilySpecs.SpecName(target) +
                        "'. [Action: replace with a definition of the same spec]";
                return null;
            }

            var entry = new ReplacePlanEntry
            {
                Source = sourceRef,
                Target = targetRef,
                SourceName = sourceName,
                // Already-on-target replacements keep the current name: nothing will change.
                TargetName = alreadyOnTarget ? sourceName : target.Name,
                TargetGuid = target.GUID,
                TargetDefinition = target,
                GroupTypeId = source.Definition.GetGroupTypeId(),
                IsInstance = source.IsInstance,
                Storage = AgentFamilySpecs.StorageTypeName(source.StorageType),
                AlreadyOnTarget = alreadyOnTarget
            };

            if (!alreadyOnTarget)
            {
                entry.FormulaDependents.AddRange(FindFormulaDependents(manager, sourceName));
                plan.ChangeCount++;

                if (source.IsShared)
                {
                    // #430: deleting the old SharedParameterElement in the same pass breaks
                    // nested-family geometry; the orphan is inert and PurgeUnused removes it.
                    plan.Warnings.Add(
                        "The old shared parameter definition of '" + sourceName +
                        "' stays in the document as unused after the replacement; PurgeUnused removes it later.");
                }
            }

            if (!source.IsInstance)
            {
                foreach (var type in AgentFamilyReader.GetTypes(manager))
                {
                    var snapshot = FamilyValueSnapshot.ReadFamilyType(type, source);
                    entry.TypeSnapshot.Add(new ReplaceTypeSnapshot
                    {
                        TypeName = type.Name,
                        Storage = AgentFamilySpecs.StorageTypeName(snapshot.StorageType),
                        Value = snapshot.ToRaw()
                    });
                }
            }

            plan.Entries.Add(entry);
            plan.Expected.Add(new FamilyReplaceEntryState
            {
                Source = new FamilyReplaceSourceState
                {
                    Name = sourceName,
                    Guid = sourceGuid?.ToString("D"),
                    Scope = ParameterScope(source),
                    IsShared = source.IsShared
                },
                Target = new FamilyReplaceTargetState
                {
                    Name = entry.TargetName,
                    Guid = target.GUID.ToString("D")
                },
                FormulasAffected = entry.FormulaDependents.ToList(),
                AlreadyOnTarget = alreadyOnTarget,
                Types = entry.TypeSnapshot.Select(snapshot => new FamilyReplaceTypeState
                {
                    TypeName = snapshot.TypeName,
                    Storage = snapshot.Storage,
                    Value = snapshot.Value
                }).ToList()
            });
            plan.Actual.Add(new FamilyReplaceActualState
            {
                Name = sourceName,
                Guid = sourceGuid?.ToString("D"),
                Scope = ParameterScope(source),
                IsShared = source.IsShared
            });
        }

        return plan;
    }

    /// <summary>
    /// Formulas and labels that reference the old name are re-pointed by Revit;
    /// list them up front so the preview shows the blast radius.
    /// </summary>
    private static List<string> FindFormulaDependents(RevitFamilyManager manager, string parameterName)
    {
        var dependents = new List<string>();
        foreach (var candidate in manager.GetParameters())
        {
            var formula = candidate.Formula;
            if (!string.IsNullOrWhiteSpace(formula)
                && formula!.IndexOf(parameterName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dependents.Add(candidate.Definition.Name);
            }
        }

        return dependents;
    }
}
