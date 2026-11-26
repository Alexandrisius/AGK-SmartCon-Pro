using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Creates a level by name + elevation (S.42): the "new floor" scenario was
/// impossible before (list_levels/rename_levels only), so plans could not be
/// made for a level that did not exist. Revit does NOT create a plan view for a
/// new level — create_floor_plans does that, and inside execute_plan the two
/// steps are chained with a {"$step": …} reference to created.levelId.
/// </summary>
public sealed class CreateLevelTool : AgentWriteToolBase
{
    public CreateLevelTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    // A level is not a view: it is created in the active workset.
    protected override bool RequiresActiveWorkset => true;

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_level",
        Description =
            "Creates a level with a unique name at the given elevation (millimetres, negative " +
            "below the project base). Guards: level_name_taken (a level with that name exists " +
            "— rename_levels or another name), invalid_name (level naming rules). Revit does " +
            "NOT create a plan view for the new level — follow with create_floor_plans; inside " +
            "execute_plan chain the ids: {\"$step\":\"<stepId>\",\"path\":\"created.levelId\"}. " +
            "Dry run by default: created.levelId is PROVISIONAL there and does not exist after " +
            "the rolled-back dry run.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""name"":{""type"":""string"",""minLength"":1},""elevationMm"":{""type"":""number"",""description"":""Elevation above the project base, mm (negative allowed)""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""name"",""elevationMm""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var name = AgentJson.GetString(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Fail(outcome, "invalid_arguments", "Pass name (the new level name).");
            return;
        }

        if (!AgentJson.TryGetDouble(args, "elevationMm", out var elevationMm))
        {
            Fail(outcome, "invalid_arguments",
                "Pass elevationMm (the elevation above the project base in millimetres).");
            return;
        }

        foreach (var existing in new FilteredElementCollector(doc).OfClass(typeof(Level)))
        {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                Fail(outcome, "level_name_taken",
                    "A level named '" + name + "' already exists (id " + existing.Id.GetValue() +
                    "). Choose another name or rename the existing level with rename_levels.");
                outcome.Data["owner"] = new { levelId = existing.Id.GetValue(), name = existing.Name };
                return;
            }
        }

        var nameError = AgentViewWriteHelper.ValidateName(name);
        if (nameError is not null)
        {
            Fail(outcome, "invalid_name", nameError);
            return;
        }

        outcome.Description = "Create level " + name;

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = name,
                NewValue = name,
                Detail = "Elevation " + elevationMm.ToString(
                    "F0", System.Globalization.CultureInfo.InvariantCulture) + " mm",
                Reason = "Dry run: the level would be created."
            });
            outcome.AffectedCount = 1;
            outcome.Data["created"] = new { levelId = -1L, name };
            outcome.Warnings.Add(
                "dryRun: the level was created inside the rolled-back transaction; " +
                "created.levelId is PROVISIONAL and will not exist after this call. " +
                "[Action: re-run with dryRun:false, or in a plan reference the real level with " +
                "{\"$step\":\"<stepId>\",\"path\":\"created.levelId\"}]");
            return;
        }

        Level level;
        try
        {
            level = Level.Create(
                doc,
                UnitUtils.ConvertToInternalUnits(elevationMm, UnitTypeId.Millimeters));
        }
        catch (Exception ex)
        {
            Fail(outcome, "level_create_failed",
                "Revit refused to create the level: " + ex.Message);
            return;
        }

        try
        {
            level.Name = name;
        }
        catch (Exception ex)
        {
            Fail(outcome, "name_failed",
                "The level was created (id " + level.Id.GetValue() + ") but the name '" + name +
                "' was refused: " + ex.Message + ". [Action: rename the level with " +
                "rename_levels]");
            return;
        }

        var appliedName = level.Name;
        var levelId = level.Id.GetValue();
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = levelId,
            Name = appliedName,
            NewValue = appliedName,
            Detail = "Elevation " + elevationMm.ToString(
                "F0", System.Globalization.CultureInfo.InvariantCulture) + " mm"
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = level.UniqueId,
            ElementId = levelId,
            ParameterName = "level_created",
            NewValue = appliedName,
            Status = ChangeStatus.Applied
        });
        outcome.Data["created"] = new { levelId, name = appliedName };

        if (!string.Equals(appliedName, name, StringComparison.Ordinal))
        {
            outcome.Warnings.Add(
                "Revit stored the name as '" + appliedName + "' instead of '" + name + "'.");
        }
    }

    private static void Fail(WriteOutcome outcome, string code, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = code;
        outcome.Message = message;
    }
}
