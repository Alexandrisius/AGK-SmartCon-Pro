using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Creates a view (#348): a floor plan, a section (box built from the level,
/// the point and the direction), an isometric 3D view, or a legend. Legends
/// have no creation API — the tool duplicates an existing legend view, so a
/// project without legends fails with legend_source_missing. undo_last
/// deletes the created view.
/// </summary>
public sealed class CreateViewTool : AgentWriteToolBase
{
    private const double DefaultSectionLengthMm = 10000;
    private const double DefaultSectionHeightMm = 3000;
    private const double DefaultSectionDepthMm = 10000;

    public CreateViewTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_view",
        Description =
            "Creates one view: kind=plan (ViewPlan.Create on a level), section (a section box " +
            "from levelId, pointMmX/Y, direction north/south/east/west, lengthMm, height 3 m), " +
            "3d (isometric) or legend. Legends cannot be created by the Revit API: the tool " +
            "duplicates an existing legend, so a project with no legends answers " +
            "legend_source_missing. viewFamilyTypeId is optional (default: the first matching " +
            "ViewFamilyType). Guards: view_name_taken, level_not_found, " +
            "view_family_type_missing. undo_last deletes the created view. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""plan"",""section"",""3d"",""legend""],""description"":""Kind of the view to create""},""name"":{""type"":""string"",""description"":""Name of the new view""},""levelId"":{""type"":""integer"",""description"":""plan/section: level (list_levels)""},""viewFamilyTypeId"":{""type"":""integer"",""description"":""Optional ViewFamilyType id""},""pointMmX"":{""type"":""number"",""description"":""section: model X of the section box, mm""},""pointMmY"":{""type"":""number"",""description"":""section: model Y of the section box, mm""},""pointMmZ"":{""type"":""number"",""description"":""section: offset of the box bottom above the level, mm (default 0)""},""direction"":{""type"":""string"",""enum"":[""north"",""south"",""east"",""west""],""default"":""north"",""description"":""section: view direction""},""lengthMm"":{""type"":""number"",""default"":10000,""description"":""section: section line length, mm""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""kind"",""name""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var kind = AgentJson.GetString(args, "kind")?.Trim().ToLowerInvariant();
        var name = AgentJson.GetString(args, "name")?.Trim();
        if (kind is not ("plan" or "section" or "3d" or "legend"))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "kind must be one of: plan, section, 3d, legend.";
            return;
        }

        var nameError = AgentViewWriteHelper.ValidateName(name);
        if (nameError is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_name";
            outcome.Message = nameError;
            return;
        }

        if (AgentViewWriteHelper.CollectViewNamesByName(doc).ContainsKey(name!))
        {
            outcome.Success = false;
            outcome.ErrorCode = "view_name_taken";
            outcome.Message = "The name '" + name + "' is already used by a view/sheet. " +
                              "Pass a different name.";
            return;
        }

        outcome.Description = "Create " + kind + " view '" + name + "'";

        View? created = null;
        try
        {
            created = kind switch
            {
                "plan" => CreatePlan(doc, args, name!, outcome),
                "section" => CreateSection(doc, args, name!, outcome),
                "3d" => Create3D(doc, args, name!, outcome),
                _ => CreateLegend(doc, name!, outcome)
            };
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            FailCreation(outcome, ex.GetType().Name + ": " + ex.Message);
        }
        catch (Exception ex)
        {
            FailCreation(outcome, ex.GetType().Name + ": " + ex.Message);
        }

        if (!outcome.Success || created is null)
            return;

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = created.Name,
                NewValue = name,
                Reason = "Dry run: the " + kind + " view would be created as '" + name + "'."
            });
            outcome.AffectedCount = 1;
            return;
        }

        try
        {
            created.Name = name!;
        }
        catch (Exception ex)
        {
            outcome.Warnings.Add(
                "The view was created but could not be renamed to '" + name +
                "' (it keeps the name '" + created.Name + "'): " + ex.Message +
                " [Action: rename it with rename_views]");
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = created.Id.GetValue(),
            Name = created.Name,
            NewValue = created.Name,
            Detail = kind
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = created.UniqueId,
            ElementId = created.Id.GetValue(),
            ParameterName = "view_created",
            NewValue = created.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["viewId"] = created.Id.GetValue();
        outcome.Data["name"] = created.Name;
        outcome.Data["kind"] = kind;
        var genLevel = (created as ViewPlan)?.GenLevel;
        outcome.Data["levelId"] = genLevel is null ? null : genLevel.Id.GetValue();
    }

    private static void FailCreation(WriteOutcome outcome, string reason)
    {
        outcome.Success = false;
        outcome.ErrorCode ??= "view_creation_failed";
        outcome.Message = "Revit refused to create the view: " + reason;
    }

    private static View CreatePlan(Document doc, JsonElement args, string name, WriteOutcome outcome)
    {
        var level = ResolveLevel(doc, args, outcome);
        if (level is null)
            return null!;

        var familyType = ResolveFamilyType(doc, args, ViewFamily.FloorPlan, outcome);
        if (familyType is null)
            return null!;

        return ViewPlan.Create(doc, familyType.Id, level.Id);
    }

    private static View CreateSection(Document doc, JsonElement args, string name, WriteOutcome outcome)
    {
        var level = ResolveLevel(doc, args, outcome);
        if (level is null)
            return null!;

        var familyType = ResolveFamilyType(doc, args, ViewFamily.Section, outcome);
        if (familyType is null)
            return null!;

        if (!TryGetDirection(AgentJson.GetString(args, "direction"), out var direction))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "direction must be one of: north, south, east, west.";
            return null!;
        }

        AgentJson.TryGetDouble(args, "pointMmX", out var pointMmX);
        AgentJson.TryGetDouble(args, "pointMmY", out var pointMmY);
        AgentJson.TryGetDouble(args, "pointMmZ", out var pointMmZ);
        AgentJson.TryGetDouble(args, "lengthMm", out var lengthMm);
        if (lengthMm <= 0)
            lengthMm = DefaultSectionLengthMm;

        var length = ToInternalLength(lengthMm);
        var height = ToInternalLength(DefaultSectionHeightMm);
        var depth = ToInternalLength(DefaultSectionDepthMm);

        var box = new BoundingBoxXYZ
        {
            Transform = CreateSectionTransform(level.Elevation + ToInternalLength(pointMmZ),
                ToInternalLength(pointMmX), ToInternalLength(pointMmY), direction),
            Min = new XYZ(-length / 2, 0, -depth / 2),
            Max = new XYZ(length / 2, height, depth / 2)
        };

        return ViewSection.CreateSection(doc, familyType.Id, box);
    }

    private static Autodesk.Revit.DB.Transform CreateSectionTransform(
        double elevation,
        double x,
        double y,
        XYZ direction)
    {
        // CreateBasisAtOrigin exists only in Revit 2025+; the mutable Identity
        // assignment works on every supported version (net48 included).
        var transform = Autodesk.Revit.DB.Transform.Identity;
        transform.BasisX = direction.CrossProduct(XYZ.BasisZ).Normalize();
        transform.BasisY = XYZ.BasisZ;
        transform.BasisZ = direction;
        transform.Origin = new XYZ(x, y, elevation);
        return transform;
    }

    private static View Create3D(Document doc, JsonElement args, string name, WriteOutcome outcome)
    {
        var familyType = ResolveFamilyType(doc, args, ViewFamily.ThreeDimensional, outcome);
        if (familyType is null)
            return null!;

        return View3D.CreateIsometric(doc, familyType.Id);
    }

    /// <summary>
    /// The Revit API cannot create a legend view (CF-759): the working recipe
    /// is duplicating an existing legend without detailing.
    /// </summary>
    private static View CreateLegend(Document doc, string name, WriteOutcome outcome)
    {
        View? source = null;
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            if (element is View candidate
                && candidate.ViewType == ViewType.Legend
                && !candidate.IsTemplate)
            {
                source = candidate;
                break;
            }
        }

        if (source is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "legend_source_missing";
            outcome.Message = "The project has no legend view to duplicate: the Revit API " +
                              "cannot create legends from scratch. Create one blank legend in " +
                              "Revit (View > Legends > Legend), then create_view can duplicate " +
                              "it.";
            return null!;
        }

        if (!source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
        {
            outcome.Success = false;
            outcome.ErrorCode = "view_not_duplicatable";
            outcome.Message = "The source legend '" + source.Name + "' cannot be duplicated.";
            return null!;
        }

        var copyId = source.Duplicate(ViewDuplicateOption.Duplicate);
        return doc.GetElement(copyId) as View ?? null!;
    }

    private static Level? ResolveLevel(Document doc, JsonElement args, WriteOutcome outcome)
    {
        var levelId = ReadLong(args, "levelId");
        if (levelId.HasValue)
        {
            if (AgentViewWriteHelper.TryGetLevel(
                    doc, levelId.Value, out var level, out var reason))
            {
                return level;
            }

            outcome.Success = false;
            outcome.ErrorCode = "level_not_found";
            outcome.Message = reason;
            return null;
        }

        outcome.Success = false;
        outcome.ErrorCode = "level_not_found";
        outcome.Message = "Pass levelId (list_levels) for a plan/section view.";
        return null;
    }

    private static ViewFamilyType? ResolveFamilyType(
        Document doc,
        JsonElement args,
        ViewFamily family,
        WriteOutcome outcome)
    {
        var requestedId = ReadLong(args, "viewFamilyTypeId");
        if (requestedId.HasValue
            && doc.GetElement(ElementIdCompat.Create(requestedId.Value)) is ViewFamilyType requested
            && requested.ViewFamily == family)
        {
            return requested;
        }

        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)))
        {
            if (element is ViewFamilyType candidate && candidate.ViewFamily == family)
                return candidate;
        }

        outcome.Success = false;
        outcome.ErrorCode = "view_family_type_missing";
        outcome.Message = "The document has no ViewFamilyType of family " + family +
                          " (needed for this kind of view). Load a family that provides it.";
        return null;
    }

    private static bool TryGetDirection(string? text, out XYZ direction)
    {
        switch (text)
        {
            case null:
            case "":
            case "north":
                direction = new XYZ(0, 1, 0);
                return true;
            case "south":
                direction = new XYZ(0, -1, 0);
                return true;
            case "east":
                direction = new XYZ(1, 0, 0);
                return true;
            case "west":
                direction = new XYZ(-1, 0, 0);
                return true;
            default:
                direction = XYZ.Zero;
                return false;
        }
    }

    private static double ToInternalLength(double millimeters) =>
        UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);

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
