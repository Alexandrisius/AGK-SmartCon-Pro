using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Creates a schedule for one category with optional initial fields. Not
/// idempotent: an existing schedule name fails with schedule_name_taken.
/// </summary>
public sealed class CreateScheduleTool : AgentWriteToolBase
{
    private const int AvailableFieldsHint = 20;

    public CreateScheduleTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_schedule",
        Description =
            "Creates a new schedule (specification) for one category: category is the BuiltIn " +
            "name (OST_Pipes), the localized display name or a categoryId from list_categories. " +
            "category:\"multicategory\" (or categoryId -1) creates a schedule ACROSS categories: " +
            "its fields are the ones common to all of them (shared parameters); a built-in that " +
            "exists only on some categories cannot be a field. " +
            "name must be unique (a duplicate fails with schedule_name_taken). fields (0..15) " +
            "are schedulable field display names as get_schedule_definition reports them; an " +
            "unknown field fails per item with field_not_available and availableFields[]. " +
            "itemized=true (default) shows each element on its own row. Returns " +
            "created{scheduleId, name} and the read-back field list; in a dry run the " +
            "scheduleId is PROVISIONAL — the schedule was built inside the rolled-back " +
            "transaction and does not exist, so do not reference it (re-run with " +
            "dryRun:false and read the real id from the result). Not idempotent; dry run " +
            "defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""category"":{""type"":""string"",""description"":""Category: OST_Pipes, localized name, or multicategory for a schedule across categories (shared fields only)""},""categoryId"":{""type"":""integer"",""description"":""Numeric category id from list_categories; -1 = multicategory""},""name"":{""type"":""string"",""description"":""Unique schedule name, e.g. Спецификация труб""},""fields"":{""type"":""array"",""maxItems"":15,""items"":{""type"":""string""},""description"":""Initial field display names (as in get_schedule_definition)""},""itemized"":{""type"":""boolean"",""default"":true},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""name""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var name = AgentJson.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass name (the new schedule name).";
            return;
        }

        if (!AgentScheduleWriteHelper.TryResolveCategory(
                doc, args, out var categoryId, out var categoryError, out var categoryMessage))
        {
            outcome.Success = false;
            outcome.ErrorCode = categoryError;
            outcome.Message = categoryMessage;
            return;
        }

        var trimmedName = name!.Trim();
        foreach (var existing in AgentScheduleReader.Collect(doc))
        {
            if (string.Equals(existing.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
            {
                outcome.Success = false;
                outcome.ErrorCode = "schedule_name_taken";
                outcome.Message = "Schedule '" + trimmedName + "' already exists (id " +
                                  existing.Id.GetValue() + "). Choose another name.";
                outcome.Data["owner"] = new
                {
                    scheduleId = existing.Id.GetValue(),
                    name = existing.Name
                };
                return;
            }
        }

        var fields = AgentJson.GetStringArray(args, "fields");
        if (fields.Count > AgentScheduleWriteHelper.MaxFieldsPerCall)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "At most " + AgentScheduleWriteHelper.MaxFieldsPerCall +
                              " fields can be passed per call.";
            return;
        }

        ViewSchedule schedule;
        try
        {
            schedule = ViewSchedule.CreateSchedule(doc, categoryId);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_category";
            outcome.Message = "Revit refused a schedule for this category: " + ex.Message;
            return;
        }

        schedule.Name = trimmedName;
        var definition = schedule.Definition;
        try
        {
            definition.IsItemized = AgentJson.GetBool(args, "itemized", true);
        }
        catch (Exception)
        {
            // Key schedules refuse IsItemized; a fresh schedule never is one.
        }

        outcome.Description = "Create schedule " + trimmedName;

        var addedFields = new List<string>();
        foreach (var fieldName in fields)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Name = fieldName };
            if (AgentScheduleWriteHelper.TryFindSchedulableField(
                    doc, definition, fieldName, out var schedulable))
            {
                try
                {
                    definition.AddField(schedulable!);
                    addedFields.Add(fieldName);
                    report.NewValue = fieldName;
                    outcome.Batch.Changed.Add(report);
                }
                catch (Exception ex)
                {
                    report.ErrorCode = "field_not_available";
                    report.Reason = "Revit refused field '" + fieldName + "': " + ex.Message;
                    outcome.Batch.Failed.Add(report);
                }

                continue;
            }

            report.ErrorCode = "field_not_available";
            report.Reason = AgentScheduleWriteHelper.FieldNotAvailableReason(fieldName);
            outcome.Batch.Failed.Add(report);
            if (!outcome.Data.ContainsKey("availableFields"))
            {
                outcome.Data["availableFields"] = AgentScheduleWriteHelper.AvailableFieldNames(
                    doc, definition, AvailableFieldsHint);
            }
        }

        var scheduleId = schedule.Id.GetValue();
        var appliedName = schedule.Name;
        var readBackFields = AgentScheduleWriteHelper.CurrentFieldNames(definition);

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = scheduleId,
            Name = appliedName,
            NewValue = appliedName,
            Detail = "Category: " + CategoryLabel(doc, definition) + "; fields: " +
                     readBackFields.Count
        });
        outcome.AffectedCount++;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = scheduleId,
            ParameterName = "schedule_created",
            NewValue = appliedName,
            Status = ChangeStatus.Applied
        });
        // created{scheduleId, name} is the honest provisional-id marker (C7-05):
        // the execute_plan preview renames it to previewScheduleId, and the
        // dry-run warning below keeps direct calls honest too.
        outcome.Data["created"] = new { scheduleId, name = appliedName };
        outcome.Data["schedule"] = new
        {
            categoryId = categoryId.GetValue(),
            categoryName = CategoryLabel(doc, definition),
            itemized = SafeItemized(definition),
            fields = readBackFields
        };
        outcome.Data["addedFields"] = addedFields;
        if (dryRun)
        {
            outcome.Warnings.Add(
                "dryRun: the schedule was created inside the rolled-back transaction; " +
                "created.scheduleId " + scheduleId + " is PROVISIONAL and will not exist " +
                "after this call. [Action: re-run with dryRun:false and read the real " +
                "scheduleId from the result before referencing it]");
        }
    }

    private static string? CategoryLabel(Document doc, ScheduleDefinition definition)
    {
        try
        {
            var id = definition.CategoryId;
            return id is null || id == ElementId.InvalidElementId
                ? null
                : Category.GetCategory(doc, id)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SafeItemized(ScheduleDefinition definition)
    {
        try
        {
            return definition.IsItemized;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
