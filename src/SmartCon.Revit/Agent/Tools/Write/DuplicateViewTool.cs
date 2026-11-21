using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Duplicates one view with View.Duplicate (#348): with_detailing keeps the
/// annotations, dependent creates a dependent plan, without_detailing the
/// model only. The copy is renamed to newName (default: source + " (копия)").
/// undo_last deletes the created view.
/// </summary>
public sealed class DuplicateViewTool : AgentWriteToolBase
{
    public DuplicateViewTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "duplicate_view",
        Description =
            "Duplicates one view (plan, section, 3D, drafting, legend) with its settings: " +
            "option=with_detailing (default, annotations and detail copied), dependent (a " +
            "dependent plan, plans only) or without_detailing (model only). newName defaults to " +
            "the source name + ' (копия)'. Guards: view_not_duplicatable " +
            "(CanViewBeDuplicated), dependent_not_supported (dependent is for plans), " +
            "view_name_taken, view_is_template. undo_last deletes the copy. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer"",""description"":""Source view id (list_views)""},""viewName"":{""type"":""string"",""description"":""Source view name, when viewId is unknown""},""newName"":{""type"":""string"",""description"":""Name of the copy (default: source + ' (копия)')""},""option"":{""type"":""string"",""enum"":[""with_detailing"",""dependent"",""without_detailing""],""default"":""with_detailing""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var option = ParseOption(AgentJson.GetString(args, "option"), out var optionError);
        if (optionError is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = optionError;
            return;
        }

        if (!TryResolveView(doc, args, out var view, out var resolveCode, out var resolveReason))
        {
            outcome.Success = false;
            outcome.ErrorCode = resolveCode;
            outcome.Message = resolveReason;
            return;
        }

        if (view.IsTemplate)
        {
            outcome.Success = false;
            outcome.ErrorCode = "view_is_template";
            outcome.Message = "View '" + view.Name + "' is a view template; templates cannot be " +
                              "duplicated here.";
            return;
        }

        if (option == ViewDuplicateOption.AsDependent && view is not ViewPlan)
        {
            outcome.Success = false;
            outcome.ErrorCode = "dependent_not_supported";
            outcome.Message = "Dependent copies exist for plan views only; the view '" +
                              view.Name + "' is a " + view.ViewType +
                              ". Pass option=with_detailing or without_detailing.";
            return;
        }

        if (!view.CanViewBeDuplicated(option))
        {
            outcome.Success = false;
            outcome.ErrorCode = "view_not_duplicatable";
            outcome.Message = "The view '" + view.Name + "' (" + view.ViewType +
                              ") cannot be duplicated with option '" +
                              Describe(option) + "'.";
            return;
        }

        var newName = AgentJson.GetString(args, "newName")?.Trim();
        if (string.IsNullOrEmpty(newName))
            newName = view.Name + " (копия)";

        var nameError = AgentViewWriteHelper.ValidateName(newName);
        if (nameError is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_name";
            outcome.Message = nameError;
            return;
        }

        if (AgentViewWriteHelper.CollectViewNamesByName(doc)
            .TryGetValue(newName!, out var owner) && owner != view.Id.GetValue())
        {
            outcome.Success = false;
            outcome.ErrorCode = "view_name_taken";
            outcome.Message = "The name '" + newName + "' is already used by view/sheet id " +
                              owner + ". Pass a different newName.";
            return;
        }

        outcome.Description = "Duplicate view " + view.Name;
        outcome.Data["sourceViewId"] = view.Id.GetValue();
        outcome.Data["option"] = Describe(option);

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = view.Id.GetValue(),
                Name = view.Name,
                NewValue = newName,
                Reason = "Dry run: the view would be duplicated as '" + newName +
                         "' (" + Describe(option) + ")."
            });
            outcome.AffectedCount = 1;
            return;
        }

        ElementId copyId;
        try
        {
            copyId = view.Duplicate(option);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "view_not_duplicatable";
            outcome.Message = "Revit refused to duplicate the view '" + view.Name + "': " +
                              ex.GetType().Name + ": " + ex.Message;
            return;
        }

        if (copyId == ElementId.InvalidElementId || doc.GetElement(copyId) is not View copy)
        {
            outcome.Success = false;
            outcome.ErrorCode = "view_not_duplicatable";
            outcome.Message = "Revit returned no view for the duplication of '" + view.Name + "'.";
            return;
        }

        if (!string.Equals(copy.Name, newName, StringComparison.Ordinal))
        {
            try
            {
                copy.Name = newName!;
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "The copy was created but could not be renamed to '" + newName +
                    "' (it keeps the name '" + copy.Name + "'): " + ex.Message +
                    " [Action: rename it with rename_views]");
            }
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = copy.Id.GetValue(),
            Name = copy.Name,
            OldValue = view.Name,
            NewValue = copy.Name,
            Detail = Describe(option)
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = copy.UniqueId,
            ElementId = copy.Id.GetValue(),
            ParameterName = "view_created",
            OldValue = view.Name,
            NewValue = copy.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["viewId"] = copy.Id.GetValue();
        outcome.Data["name"] = copy.Name;
        outcome.Data["templateId"] = copy.ViewTemplateId == ElementId.InvalidElementId
            ? null
            : copy.ViewTemplateId.GetValue();
        outcome.Data["templateMatchesSource"] =
            copy.ViewTemplateId == view.ViewTemplateId;
    }

    internal static bool TryResolveView(
        Document doc,
        JsonElement args,
        out View view,
        out string errorCode,
        out string reason)
    {
        view = null!;
        var viewId = ReadLong(args, "viewId");
        if (viewId.HasValue && viewId.Value > 0)
        {
            if (!AgentViewWriteHelper.TryGetView(doc, viewId.Value, out var byId, out errorCode, out reason))
                return false;

            view = byId;
            return true;
        }

        var viewName = AgentJson.GetString(args, "viewName")?.Trim();
        if (string.IsNullOrWhiteSpace(viewName))
        {
            errorCode = "invalid_arguments";
            reason = "Pass viewId or viewName.";
            return false;
        }

        if (!AgentViewWriteHelper.CollectViewNamesByName(doc)
            .TryGetValue(viewName!, out var foundId))
        {
            errorCode = "view_not_found";
            reason = "No view named '" + viewName + "' exists in the document.";
            return false;
        }

        if (doc.GetElement(ElementIdCompat.Create(foundId)) is not View byName)
        {
            errorCode = "view_not_found";
            reason = "View '" + viewName + "' disappeared during the operation.";
            return false;
        }

        view = byName;
        errorCode = string.Empty;
        reason = string.Empty;
        return true;
    }

    internal static ViewDuplicateOption ParseOption(string? text, out string? error)
    {
        error = null;
        switch (text)
        {
            case null:
            case "":
            case "with_detailing":
                return ViewDuplicateOption.WithDetailing;
            case "dependent":
                return ViewDuplicateOption.AsDependent;
            case "without_detailing":
                // The plain Duplicate option exists in every Revit version and
                // means the same thing: model and settings without annotation.
                return ViewDuplicateOption.Duplicate;
            default:
                error = "option must be one of: with_detailing, dependent, without_detailing.";
                return ViewDuplicateOption.WithDetailing;
        }
    }

    internal static string Describe(ViewDuplicateOption option) => option switch
    {
        ViewDuplicateOption.AsDependent => "dependent",
        ViewDuplicateOption.Duplicate => "without_detailing",
        _ => "with_detailing"
    };

    private static long? ReadLong(JsonElement args, string property)
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
}
