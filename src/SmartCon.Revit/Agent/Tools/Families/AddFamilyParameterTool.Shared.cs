using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Shared-definition half of <see cref="AddFamilyParameterTool"/>: resolves an
/// existing definition in the active shared parameter file, creates a missing
/// one only on <c>createDefinition=true</c>, and suggests close names otherwise.
/// </summary>
public sealed partial class AddFamilyParameterTool
{
    private static ExternalDefinition? ResolveSharedDefinition(
        AgentFamilySession session,
        string name,
        ForgeTypeId spec,
        Guid? guid,
        bool createDefinition,
        bool dryRun,
        WriteOutcome outcome,
        out bool wouldCreateDefinition)
    {
        wouldCreateDefinition = false;
        DefinitionFile? file;
        try
        {
            file = session.ProjectDoc.Application.OpenSharedParameterFile();
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter file could not be opened: " + ex.Message,
                "[Action: configure a shared parameter file in Manage > Shared Parameters " +
                "and retry]");
            return null;
        }

        if (file is null)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "No shared parameter file is configured, so a shared parameter cannot be " +
                "created.",
                "[Action: configure a shared parameter file in Manage > Shared Parameters " +
                "and retry]");
            return null;
        }

        ExternalDefinition? existing = null;
        try
        {
            foreach (DefinitionGroup candidate in file.Groups)
            {
                foreach (Definition definition in candidate.Definitions)
                {
                    if (definition is not ExternalDefinition external)
                        continue;

                    var sameGuid = guid.HasValue && external.GUID == guid.Value;
                    var sameName = string.Equals(
                        external.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase);
                    if (!sameGuid && !sameName)
                        continue;

                    existing = external;
                    break;
                }

                if (existing is not null)
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter file could not be read: " + ex.Message,
                "[Action: check the shared parameter file and retry]");
            return null;
        }

        if (existing is not null)
        {
            if (!existing.GetDataType().Equals(spec))
            {
                Fail(
                    outcome,
                    "invalid_value",
                    "The shared parameter definition '" + existing.Name + "' already exists " +
                    "with a different data type.",
                    "Use another name or fix the definition in the shared parameter file.");
                return null;
            }

            if (guid.HasValue && existing.GUID != guid.Value)
            {
                Fail(
                    outcome,
                    "invalid_value",
                    "The shared parameter definition '" + existing.Name + "' already exists " +
                    "with GUID " + existing.GUID.ToString("D") + ", not " +
                    guid.Value.ToString("D") + ".",
                    "Drop guid to reuse the existing definition, or use another name.");
                return null;
            }

            return existing;
        }

        if (!createDefinition)
        {
            // Default: never write into the active shared parameter file. A typo in a
            // customer's ФОП cannot be rolled back (external file), so a missing
            // definition is reported with close matches instead.
            var suggestions = SuggestDefinitions(file, name);
            Fail(
                outcome,
                "definition_not_found",
                "The active shared parameter file has no definition named '" + name + "'" +
                (guid.HasValue ? " with GUID " + guid.Value.ToString("D") : string.Empty) + ".",
                "Existing definitions are listed by list_shared_parameters" +
                (suggestions.Count == 0
                    ? "."
                    : "; closest: " + string.Join(", ", suggestions) + ".") +
                " Pass createDefinition=true to add the definition to " + file.Filename +
                " (this changes an external file and cannot be undone by undo_last).");
            return null;
        }

        if (dryRun)
        {
            wouldCreateDefinition = true;
            return null;
        }

        DefinitionGroup? group;
        try
        {
            group = file.Groups.get_Item("SmartCon") ?? file.Groups.Create("SmartCon");
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter group could not be created: " + ex.Message,
                "[Action: check that the shared parameter file is writable]");
            return null;
        }

        if (group is null)
        {
            Fail(outcome, "shared_parameter_file_missing",
                "The shared parameter group 'SmartCon' could not be created.");
            return null;
        }

        try
        {
            var options = new ExternalDefinitionCreationOptions(name, spec);
            if (guid.HasValue)
                options.GUID = guid.Value;

            var created = group.Definitions.Create(options) as ExternalDefinition;
            if (created is null)
            {
                Fail(
                    outcome,
                    "shared_parameter_file_missing",
                    "Revit did not create the shared parameter definition.",
                    "[Action: check the shared parameter file and retry]");
            }
            else
            {
                // The external file is not part of the family transaction: state the
                // side effect up front so a rolled-back family never hides it.
                outcome.Data["sharedDefinitionCreatedInFile"] = true;
                outcome.Warnings.Add(
                    "The shared parameter definition '" + name + "' was written to the " +
                    "shared parameter file (" + System.IO.Path.GetFileName(file.Filename) +
                    ") before the family transaction committed; undo_last cannot revert " +
                    "this external file change.");
            }

            return created;
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter definition could not be created: " + ex.Message,
                "[Action: check that the shared parameter file is writable and has no " +
                "duplicate names]");
            return null;
        }
    }

    private static List<string> SuggestDefinitions(DefinitionFile file, string name)
    {
        var candidates = new List<string>();
        try
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is ExternalDefinition external
                        && !string.IsNullOrWhiteSpace(external.Name))
                    {
                        candidates.Add(external.Name);
                    }
                }
            }
        }
        catch (Exception)
        {
            return new List<string>();
        }

        var normalized = name.Trim().ToLowerInvariant();
        var threshold = System.Math.Max(3, normalized.Length / 2);
        return candidates
            .Select(candidate => (
                Name: candidate,
                Distance: Distance(normalized, candidate.ToLowerInvariant())))
            .Where(pair => pair.Distance <= threshold)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Name, StringComparer.Ordinal)
            .Take(5)
            .Select(pair => pair.Name)
            .ToList();
    }

    private static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = System.Math.Min(
                    System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
    }

    private static void Fail(
        WriteOutcome outcome,
        string errorCode,
        string message,
        string? hint = null)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = string.IsNullOrEmpty(hint) ? message : message + " " + hint;
        outcome.Batch.Failed.Add(new WriteItemReport
        {
            ErrorCode = errorCode,
            Reason = message
        });
    }
}