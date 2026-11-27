using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Adds family parameters (normal or shared, instance or type) to every type of
/// the family. Shared parameters are created through the shared parameter file;
/// the GUID is preserved and returned. A batch (<c>items[]</c>) runs in ONE
/// family open, ONE transaction and ONE load-back: N parameters used to cost N
/// opens, N backups and N reloads, and every reload changed familyId.
/// </summary>
public sealed partial class AddFamilyParameterTool : AgentFamilyWriteToolBase
{
    private const int MaxExistenceChecks = 20;
    private const int MaxNameLength = 255;
    private const int MaxBatchItems = 50;

    public AddFamilyParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "add_family_parameter",
        Description =
            "Adds parameters to a family: normal or shared (customer shared-parameter definitions), " +
            "instance or type, with a parameter group and storage type. Pass ONE parameter as " +
            "flat fields or MANY as items[] (up to 50) - a batch costs one family open, one " +
            "transaction and one load-back, not one per parameter. For a shared parameter the " +
            "definition must already exist in the active shared parameter file " +
            "(list_shared_parameters shows what is there); pass createDefinition=true to add a " +
            "missing definition to that file. Checks duplicate names, preserves the GUID and " +
            "reports every parameter separately. Dry run by default. " +
            "formula (flat or per item) assigns an AUTHORED formula right after the " +
            "creation — the migration unit is then add + formula + read-back. A rejected " +
            "formula fails its item with invalid_formula and the parameter is removed again " +
            "(a single-item call rolls back whole; in a batch the other items stand).",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""name"":{""type"":""string"",""description"":""Single-parameter form; omit when items is passed""},""storageType"":{""type"":""string"",""description"":""length | ductSize | pipeSize | distance | area | volume | angle | number | integer | text | yesno | material""},""group"":{""type"":""string"",""description"":""Revit group id, e.g. PG_GENERAL, PG_IDENTITY_DATA, PG_TEXT, PG_CONSTRAINTS, PG_DATA; localized Russian names are accepted""},""isInstance"":{""type"":""boolean"",""default"":false},""isShared"":{""type"":""boolean"",""default"":false},""createDefinition"":{""type"":""boolean"",""default"":false,""description"":""Create a missing shared definition in the active shared parameter file; false reports definition_not_found""},""guid"":{""type"":""string""},""initialValue"":{""type"":""string""},""unit"":{""type"":""string""},""formula"":{""type"":""string"",""description"":""Authored formula assigned right after creation (add + formula + verify in one unit); unit literals neutralize a dimension (X / 1 kg)""},""items"":{""type"":""array"",""minItems"":1,""maxItems"":50,""description"":""Batch form: every parameter of this family in ONE open/transaction/reload; per-item changed/skipped/failed report"",""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""storageType"":{""type"":""string""},""group"":{""type"":""string""},""isInstance"":{""type"":""boolean"",""default"":false},""isShared"":{""type"":""boolean"",""default"":false},""createDefinition"":{""type"":""boolean"",""default"":false},""guid"":{""type"":""string""},""initialValue"":{""type"":""string""},""unit"":{""type"":""string""},""formula"":{""type"":""string""}},""required"":[""name"",""storageType""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<AddFamilyParameterBatchRequest>(args);
        var items = request?.Items is { Count: > 0 }
            ? request.Items!
            : new List<AddFamilyParameterRequest> { request ?? new AddFamilyParameterRequest() };

        if (items.Count > MaxBatchItems)
        {
            Fail(
                outcome,
                "invalid_value",
                "Too many parameters: " + items.Count + " (maximum " + MaxBatchItems + " per call).",
                "Split the batch into several calls.");
            return;
        }

        var manager = session.FamilyDoc.FamilyManager;
        var applied = 0;
        var failed = 0;
        for (var index = 0; index < items.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var label = items.Count == 1 ? null : "item " + (index + 1);
            if (AddOne(session, manager, items[index], outcome, dryRun, label))
                applied++;
            else
                failed++;
        }

        outcome.AffectedCount = applied;
        if (items.Count > 1)
        {
            // The per-item truth is changed[]/failed[]; a single "parameter"
            // object would silently stand for the last item of the batch.
            outcome.Data.Remove("parameter");
        }

        if (failed == 0)
            return;

        outcome.Success = false;
        if (applied > 0)
        {
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = applied + " parameter(s) added, " + failed +
                              " item(s) failed. See failed[] for reasons.";
            return;
        }

        // A single-parameter call keeps the old shape: the code and message of
        // its one failure read as the call's own.
        var first = outcome.Batch.Failed.Count > 0 ? outcome.Batch.Failed[0] : null;
        outcome.ErrorCode ??= first?.ErrorCode ?? "invalid_value";
        outcome.Message ??= first?.Reason ?? "No parameter was added.";
    }

    private bool AddOne(
        AgentFamilySession session,
        RevitFamilyManager manager,
        AddFamilyParameterRequest request,
        WriteOutcome outcome,
        bool dryRun,
        string? label)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            Failed(outcome, label, null, "invalid_value", "name is required.");
            return false;
        }

        var name = request.Name!.Trim();
        if (name.Length > MaxNameLength)
        {
            Failed(
                outcome,
                label,
                name,
                "invalid_value",
                "The parameter name is " + name.Length + " characters long; names are " +
                "limited to " + MaxNameLength + " characters.");
            return false;
        }

        if (!AgentFamilySpecs.TryResolveSpec(
                request.StorageType,
                out var spec,
                out var kind,
                out var specMessage))
        {
            Failed(
                outcome,
                label,
                name,
                "invalid_value",
                specMessage ?? "Unknown storageType.");
            return false;
        }

        if (!AgentFamilySpecs.TryResolveGroup(request.Group, out var group, out var groupMessage))
        {
            Failed(
                outcome,
                label,
                name,
                "invalid_value",
                groupMessage ?? "Unknown parameter group.");
            return false;
        }

        foreach (var existing in manager.GetParameters())
        {
            if (string.Equals(existing.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                Failed(
                    outcome,
                    label,
                    name,
                    "parameter_exists",
                    "The family already has a parameter named '" + name + "' (" +
                    (existing.IsInstance ? "instance" : "type") + "). " +
                    "Revit allows the same name as BOTH scopes: if you meant the other scope, pass scope=\""
                    + (existing.IsInstance ? "type" : "instance") + "\". "
                    + "Use rename_family_parameter to rename it or pick another name.");
                return false;
            }
        }

        // C5-01: a parameter named like a built-in (e.g. the localized 'Код по
        // классификатору' = UNIFORMAT_CODE/ASSEMBLY_CODE) is created happily by
        // Revit, shadows nothing and reads back green while every write meant
        // for the built-in keeps missing it.
        var collision = AgentParameterResolver.BuiltInNameCollision(name);
        if (collision is not null)
        {
            Failed(
                outcome,
                label,
                name,
                "parameter_name_taken",
                "The name '" + name + "' is the display name of the built-in parameter " +
                collision.Value + ": a family parameter with this name shadows nothing, and " +
                "writes meant for the built-in parameter keep going to the built-in one. " +
                "Write the built-in parameter instead — set_family_parameter_values and " +
                "set_parameters accept the name '" + name + "' or the enum id '" + collision.Value +
                "'. If a separate parameter is really needed, use a different name.");
            return false;
        }

        Guid? guid = null;
        if (!string.IsNullOrWhiteSpace(request.Guid))
        {
            if (!Guid.TryParse(request.Guid, out var parsedGuid))
            {
                Failed(
                    outcome,
                    label,
                    name,
                    "invalid_value",
                    "'" + request.Guid + "' is not a valid GUID.");
                return false;
            }

            if (!request.IsShared)
            {
                Failed(
                    outcome,
                    label,
                    name,
                    "invalid_value",
                    "guid is only valid for a shared parameter; set isShared=true or drop guid.");
                return false;
            }

            guid = parsedGuid;
            var byGuid = manager.get_Parameter(parsedGuid);
            if (byGuid is not null)
            {
                Failed(
                    outcome,
                    label,
                    name,
                    "parameter_exists",
                    "A shared parameter with GUID " + request.Guid + " already exists in the " +
                    "family as '" + byGuid.Definition.Name + "'.");
                return false;
            }
        }

        FamilyValueSnapshot? initialValue = null;
        if (!string.IsNullOrWhiteSpace(request.InitialValue))
        {
            if (kind == AgentFamilyValueKind.ElementId)
            {
                Failed(
                    outcome,
                    label,
                    name,
                    "invalid_value",
                    "initialValue is not supported for material parameters.");
                return false;
            }

            if (manager.CurrentType is null)
            {
                Failed(
                    outcome,
                    label,
                    name,
                    "empty_family",
                    "The family has no types, so an initial value cannot be set. Add the " +
                    "parameter without initialValue, or create a type first.");
                return false;
            }

            if (!AgentFamilyUnits.TryConvert(
                    kind,
                    request.InitialValue,
                    request.Unit,
                    out var converted,
                    out var valueErrorCode,
                    out var valueMessage))
            {
                Failed(
                    outcome,
                    label,
                    name,
                    valueErrorCode ?? "invalid_value",
                    valueMessage ?? "Invalid value.");
                return false;
            }

            initialValue = AgentFamilyValues.FromConverted(converted);
        }

        ExternalDefinition? sharedDefinition = null;
        var wouldCreateDefinition = false;
        if (request.IsShared)
        {
            sharedDefinition = ResolveSharedDefinition(
                session,
                name,
                spec!,
                guid,
                request.CreateDefinition,
                dryRun,
                outcome,
                out wouldCreateDefinition);
            if (sharedDefinition is null && !wouldCreateDefinition)
                return false;

            if (wouldCreateDefinition)
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Name = name,
                    NewValue = "shared definition would be created in the shared parameter file",
                    Detail = Join(label, dryRun ? "dry run: no shared parameter file change" : null)
                });
                outcome.Data["parameter"] = new
                {
                    name,
                    scope = request.IsInstance ? "instance" : "type",
                    isShared = true,
                    guid = guid?.ToString("D"),
                    storageType = StorageTypeNameOf(kind),
                    spec = AgentFamilySpecs.SpecNameOf(spec!),
                    group = AgentFamilySpecs.GroupNameOf(group!),
                    wouldCreateDefinition = true,
                    projectBinding = ProjectBindingNote.Resolve(session.ProjectDoc, guid, name)
                };
                outcome.Warnings.Add(
                    "Dry run: the shared parameter definition '" + name + "' does not exist yet; " +
                    "a real apply would create it in the shared parameter file (an external " +
                    "file that cannot be rolled back).");
                return true;
            }
        }

        FamilyParameter? created;
        try
        {
            created = request.IsShared
                ? manager.AddParameter(sharedDefinition!, group!, request.IsInstance)
                : manager.AddParameter(name, group!, spec!, request.IsInstance);
        }
        catch (Exception ex)
        {
            Failed(
                outcome,
                label,
                name,
                "invalid_value",
                "Revit refused to add the parameter: " + ex.Message +
                " Check the parameter name, group and storage type.");
            return false;
        }

        if (created is null)
        {
            Failed(outcome, label, name, "invalid_value", "Revit did not create the parameter.");
            return false;
        }

        if (initialValue.HasValue)
        {
            try
            {
                AgentFamilyValues.Set(manager, created, initialValue.Value);
            }
            catch (Exception ex)
            {
                Failed(
                    outcome,
                    label,
                    name,
                    "invalid_value",
                    "The parameter was created, but the initial value could not be set: " +
                    ex.Message);
                return false;
            }
        }

        var appliedFormula = (string?)null;
        if (!string.IsNullOrWhiteSpace(request.Formula))
        {
            appliedFormula = request.Formula!.Trim();
            if (dryRun)
            {
                outcome.Warnings.Add(
                    "Dry run: the formula '" + appliedFormula + "' would be assigned to '" +
                    name + "' on apply; Revit validates a formula only on a real assignment.");
            }
            else
            {
                var formulaError = TryApplyFormula(session, manager, created, appliedFormula);
                if (formulaError is not null)
                {
                    // Validator N1: in a mixed batch the family transaction would
                    // still COMMIT (other items succeeded), leaving this parameter
                    // half-added without its formula. Remove it again so the
                    // failed item leaves nothing behind; the rest of the batch
                    // stands (a single-item call still rolls back whole — the
                    // base sees AffectedCount == 0).
                    try
                    {
                        manager.RemoveParameter(created);
                    }
                    catch (Exception)
                    {
                        // If Revit refuses the removal the parameter stays; the
                        // failed[] text below says how to clean up.
                    }

                    Failed(
                        outcome,
                        label,
                        name,
                        "invalid_formula",
                        "The formula '" + appliedFormula + "' was rejected for '" + name +
                        "': " + formulaError + " The parameter was removed again; the other " +
                        "items of the batch stand (a single-item call rolls back whole). " +
                        "[Action: fix the formula and assign it with set_family_formula]");
                    return false;
                }
            }
        }

        var sharedText = request.IsShared
            ? "shared " + (created.IsShared ? created.GUID.ToString("D") : "?")
            : "family";
        var scopeText = created.IsInstance ? "instance" : "type";
        // Pilot v14: a project binding overrides the family's scope and group in
        // the project context, and schedules follow the BINDING's scope — surface
        // it right in the add result instead of a confusing PG_TEXT read-back.
        ProjectBindingNote? projectBinding = created.IsShared
            ? ProjectBindingNote.Resolve(session.ProjectDoc, created.GUID, created.Definition.Name)
            : null;
        if (projectBinding is not null)
        {
            var diverges = !string.Equals(
                projectBinding.Scope, scopeText, StringComparison.OrdinalIgnoreCase);
            var requestedGroupName = request.Group is not null
                                     && AgentFamilySpecs.TryResolveGroup(
                                         request.Group, out var requestedGroup, out _)
                                     && requestedGroup is not null
                ? AgentFamilySpecs.GroupNameOf(requestedGroup)
                : null;
            var groupDiffers = requestedGroupName is not null
                               && projectBinding.Group is not null
                               && !string.Equals(
                                   requestedGroupName,
                                   projectBinding.Group,
                                   StringComparison.OrdinalIgnoreCase);
            outcome.Warnings.Add(
                "Project binding of '" + created.Definition.Name + "': " + projectBinding.Scope +
                ", group " + (projectBinding.Group ?? "(unknown)") +
                " — the project context follows the BINDING, not the family (" +
                (diverges
                    ? "scopes diverge: family " + scopeText + " vs binding " + projectBinding.Scope +
                      "; schedules read the binding's scope"
                    : "the requested group shows as " + projectBinding.Group +
                      " in project read-back") + ")." +
                (groupDiffers
                    ? " [Action: to force the group, rebind the project binding: " +
                      "rebind_project_parameter(name/guid, group:'" + requestedGroupName +
                      "') — the rebind drops existing values of this parameter in the project]"
                    : string.Empty));
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = created.Definition.Name,
            OldValue = null,
            NewValue = scopeText + " " + AgentFamilySpecs.StorageTypeName(created.StorageType) +
                       " (" + sharedText + ")" +
                       (appliedFormula is null || dryRun ? string.Empty : " + formula '" + appliedFormula + "'"),
            Detail = Join(label, request.Group is null ? null : "group " + AgentFamilySpecs.GroupName(created.Definition)),
            ProjectBinding = projectBinding
        });
        session.AddUndoOperation(new FamilyUndoOperation
        {
            Kind = "add",
            Parameter = created.Definition.Name,
            Scope = scopeText
        });
        outcome.Data["parameter"] = new
        {
            name = created.Definition.Name,
            scope = scopeText,
            isShared = created.IsShared,
            guid = created.IsShared ? created.GUID.ToString("D") : null,
            storageType = AgentFamilySpecs.StorageTypeName(created.StorageType),
            spec = AgentFamilySpecs.SpecName(created.Definition),
            group = AgentFamilySpecs.GroupName(created.Definition),
            formula = dryRun ? request.Formula?.Trim() : created.Formula,
            projectBinding
        };

        var types = AgentFamilyReader.GetTypes(manager);
        for (var index = 0; index < types.Count && index < MaxExistenceChecks; index++)
        {
            session.AddExistenceExpectation(types[index].Name, created.Definition.Name, !created.IsInstance);
        }

        if (types.Count == 0)
        {
            outcome.Warnings.Add(
                "The family has no types, so the new parameter '" + created.Definition.Name +
                "' will only appear after a type is created.");
        }

        return true;
    }

    /// <summary>
    /// Assigns the authored formula; returns the rejection reason or null on
    /// success. Lookup tables are checked first so the error names the table,
    /// not a generic Revit refusal.
    /// </summary>
    private static string? TryApplyFormula(
        AgentFamilySession session,
        RevitFamilyManager manager,
        FamilyParameter parameter,
        string formula)
    {
        if (!AgentFamilyReader.TryFindMissingLookupTables(
                session.FamilyDoc,
                formula,
                out var missingTables))
        {
            return "the formula references lookup table(s) that are not present in the family: " +
                   string.Join(", ", missingTables) + ".";
        }

        try
        {
            manager.SetFormula(parameter, formula);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string StorageTypeNameOf(AgentFamilyValueKind kind) => kind switch
    {
        AgentFamilyValueKind.Length or AgentFamilyValueKind.Angle
            or AgentFamilyValueKind.Area or AgentFamilyValueKind.Volume
            or AgentFamilyValueKind.Number => "double",
        AgentFamilyValueKind.WholeNumber or AgentFamilyValueKind.YesNo => "integer",
        AgentFamilyValueKind.Text => "string",
        AgentFamilyValueKind.ElementId => "elementId",
        _ => "unknown"
    };

    private static string? Join(string? label, string? detail) =>
        string.IsNullOrEmpty(label)
            ? detail
            : string.IsNullOrEmpty(detail) ? label : label + ", " + detail;

    private static void Failed(
        WriteOutcome outcome,
        string? label,
        string? name,
        string errorCode,
        string reason)
    {
        outcome.Batch.Failed.Add(new WriteItemReport
        {
            Name = string.IsNullOrEmpty(label) ? name ?? string.Empty : label,
            ErrorCode = errorCode,
            Reason = reason
        });
    }
}