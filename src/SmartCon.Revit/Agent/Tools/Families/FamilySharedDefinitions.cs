using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Read-only lookup of an existing definition in the active shared parameter
/// file (ФОП), by GUID (priority) or name. Never writes into the file. Shared by
/// the single-family <c>replace_family_parameter</c> tool and the
/// <c>replace_family_parameter</c> job action.
/// </summary>
internal static class FamilySharedDefinitions
{
    internal sealed class Resolution
    {
        public ExternalDefinition? Definition { get; init; }

        public string? ErrorCode { get; init; }

        public string? Message { get; init; }

        public bool Ok => Definition is not null;
    }

    /// <summary>
    /// Parses the GUID (when supplied), validates that at least one of name/guid is
    /// present, then looks the definition up. A GUID that matches by name but not by
    /// value keeps being skipped, so name matches never shadow a GUID mismatch.
    /// </summary>
    internal static Resolution FindExisting(
        Document projectDoc,
        string? name,
        string? guidText)
    {
        Guid? guid = null;
        if (!string.IsNullOrWhiteSpace(guidText))
        {
            if (!Guid.TryParse(guidText!.Trim(), out var parsed))
            {
                return Fail(
                    "invalid_arguments",
                    "The shared parameter GUID '" + guidText + "' is not a valid GUID.",
                    "Pass the definition GUID from list_shared_parameters.");
            }

            guid = parsed;
        }

        if (string.IsNullOrWhiteSpace(name) && guid is null)
        {
            return Fail(
                "invalid_arguments",
                "A shared parameter name or GUID is required.",
                "list_shared_parameters lists the existing definitions.");
        }

        DefinitionFile? file;
        try
        {
            file = projectDoc.Application.OpenSharedParameterFile();
        }
        catch (Exception ex)
        {
            return Fail(
                "shared_parameter_file_missing",
                "The shared parameter file could not be opened: " + ex.Message,
                "[Action: configure a shared parameter file in Manage > Shared Parameters and retry]");
        }

        if (file is null)
        {
            return Fail(
                "shared_parameter_file_missing",
                "No shared parameter file is configured, so a shared definition cannot be resolved.",
                "[Action: configure a shared parameter file in Manage > Shared Parameters and retry]");
        }

        try
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is not ExternalDefinition external)
                        continue;

                    var sameGuid = guid.HasValue && external.GUID == guid.Value;
                    var sameName = !string.IsNullOrWhiteSpace(name)
                        && string.Equals(external.Name, name!.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (!sameGuid && !sameName)
                        continue;

                    if (guid.HasValue && external.GUID != guid.Value)
                        continue;

                    return new Resolution { Definition = external };
                }
            }
        }
        catch (Exception ex)
        {
            return Fail(
                "shared_parameter_file_missing",
                "The shared parameter file could not be read: " + ex.Message,
                "[Action: check the shared parameter file and retry]");
        }

        return Fail(
            "definition_not_found",
            "The active shared parameter file has no definition named '" + name + "'" +
            (guid.HasValue ? " with GUID " + guid.Value.ToString("D") : string.Empty) + ".",
            "Pick an existing definition from list_shared_parameters, or create it with " +
            "add_family_parameter createDefinition=true first.");
    }

    private static Resolution Fail(string code, string message, string hint) => new()
    {
        ErrorCode = code,
        Message = string.IsNullOrEmpty(hint) ? message : message + " " + hint
    };
}
