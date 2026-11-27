using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Apply-time half of createTarget mappings (#431): resolves (or idempotently
/// creates) the shared definition in the active shared parameter file and adds
/// the parameter to the family inside the unit's own transaction, after
/// BuildPlan has recorded the add undo-op (J-04).
/// </summary>
public sealed partial class MapFamilyParametersJobAction
{
    /// <summary>Typed apply failure so a createTarget problem can carry its own code.</summary>
    private sealed record PlanApplyError(string Code, string Detail);

    private static PlanApplyError ItemFailed(string detail) =>
        new(JobErrorCodes.ItemFailed, detail);

    private static PlanApplyError? EnsureSharedTargetParameter(
        RevitFamilyManager manager,
        Document projectDoc,
        MapPlanEntry entry,
        List<string> warnings)
    {
        var source = FindParameter(manager, entry.Source);
        if (source is null)
        {
            // Live v18: the nameless variant gave the agent nothing to fix.
            return ItemFailed(
                "The mapped source parameter '" + (entry.SourceName ?? entry.Source.Name) +
                "' disappeared while applying. [Action: run the preview again]");
        }

        ForgeTypeId spec;
        ForgeTypeId group;
        try
        {
            spec = source.Definition.GetDataType();
            group = source.Definition.GetGroupTypeId();
        }
        catch (Exception ex)
        {
            return ItemFailed(
                "The source parameter spec could not be read (" + ex.GetType().Name + ": " + ex.Message +
                "). [Action: run the preview again]");
        }

        Guid? targetGuid = null;
        if (!string.IsNullOrWhiteSpace(entry.Target.SharedParameterGuid))
        {
            if (!Guid.TryParse(entry.Target.SharedParameterGuid, out var parsedGuid))
            {
                return ItemFailed(
                    "target guid '" + entry.Target.SharedParameterGuid + "' is not a valid GUID. " +
                    "[Action: run the preview again with a valid guid]");
            }

            targetGuid = parsedGuid;
        }

        DefinitionFile? file;
        try
        {
            file = projectDoc.Application.OpenSharedParameterFile();
        }
        catch (Exception ex)
        {
            return new PlanApplyError(
                "shared_parameter_file_missing",
                "The shared parameter file could not be opened: " + ex.Message +
                ". [Action: configure a shared parameter file in Manage > Shared Parameters and retry]");
        }

        if (file is null)
        {
            return new PlanApplyError(
                "shared_parameter_file_missing",
                "No shared parameter file is configured, so the target parameter '" + entry.TargetName +
                "' cannot be created. [Action: configure a shared parameter file in Manage > Shared " +
                "Parameters and retry]");
        }

        ExternalDefinition? definition;
        try
        {
            definition = FindOrCreateDefinition(file, entry.TargetName, spec, targetGuid, warnings);
        }
        catch (Exception ex)
        {
            return new PlanApplyError(
                "shared_parameter_file_missing",
                "The shared parameter definition '" + entry.TargetName + "' could not be created: " +
                ex.Message + ". [Action: check that the shared parameter file is writable and has no " +
                "duplicate names]");
        }

        if (definition is null)
        {
            return new PlanApplyError(
                "shared_parameter_file_missing",
                "Revit did not create the shared parameter definition '" + entry.TargetName +
                "'. [Action: check the shared parameter file and retry]");
        }

        try
        {
            manager.AddParameter(definition, group, entry.TargetIsInstance);
        }
        catch (Exception ex)
        {
            return ItemFailed(
                "Revit refused to create the target parameter '" + entry.TargetName + "': " + ex.Message +
                ". [Action: check the mapping spec and run the preview again]");
        }

        return null;
    }

    /// <summary>
    /// Idempotent definition resolution: an existing definition (matched by GUID or
    /// name) is reused after its data type is confirmed; a missing one is created in
    /// the SmartCon group. Creating it writes to the external file, which the job
    /// undo cannot revert — the caller reports that as a warning.
    /// </summary>
    private static ExternalDefinition? FindOrCreateDefinition(
        DefinitionFile file,
        string name,
        ForgeTypeId spec,
        Guid? guid,
        List<string> warnings)
    {
        foreach (DefinitionGroup candidate in file.Groups)
        {
            foreach (Definition definition in candidate.Definitions)
            {
                if (definition is not ExternalDefinition external)
                {
                    continue;
                }

                var sameGuid = guid.HasValue && external.GUID == guid.Value;
                var sameName = string.Equals(external.Name, name, StringComparison.OrdinalIgnoreCase);
                if (!sameGuid && !sameName)
                {
                    continue;
                }

                if (!SameDataType(external.GetDataType(), spec))
                {
                    throw new InvalidOperationException(
                        "The shared parameter definition '" + external.Name + "' already exists with a " +
                        "different data type");
                }

                if (guid.HasValue && external.GUID != guid.Value)
                {
                    throw new InvalidOperationException(
                        "The shared parameter definition '" + external.Name + "' already exists with GUID " +
                        external.GUID.ToString("D") + ", not " + guid.Value.ToString("D"));
                }

                return external;
            }
        }

        var group = file.Groups.get_Item("SmartCon") ?? file.Groups.Create("SmartCon");
        if (group is null)
        {
            throw new InvalidOperationException("The shared parameter group 'SmartCon' could not be created");
        }

        var options = new ExternalDefinitionCreationOptions(name, spec);
        if (guid.HasValue)
        {
            options.GUID = guid.Value;
        }

        var created = group.Definitions.Create(options) as ExternalDefinition;
        if (created is not null)
        {
            // The external file is not part of the family transaction: state the
            // side effect so a rolled-back unit never hides it.
            warnings.Add(
                "The shared parameter definition '" + name + "' was written to the shared parameter file (" +
                Path.GetFileName(file.Filename) + "); job undo does not revert this external file change.");
        }

        return created;
    }

    private static bool SameDataType(ForgeTypeId left, ForgeTypeId right) =>
        left.Equals(right)
        || (AgentFamilySpecs.SpecKey(left).Length > 0
            && string.Equals(AgentFamilySpecs.SpecKey(left), AgentFamilySpecs.SpecKey(right),
                StringComparison.Ordinal));
}
