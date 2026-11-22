using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Title block inventory for sheet assembly: editable parameters of every
/// loaded title block type (instance parameters too when a source sheet is
/// given) plus the existing sheets and the next album number — the inputs for
/// picking a paper format parameter and continuing the numbering.
/// </summary>
public sealed class GetTitleblockOptionsTool : AgentReadToolBase
{
    public GetTitleblockOptionsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_titleblock_options",
        Description =
            "Prepares sheet creation: lists every loaded title block type with its EDITABLE " +
            "parameters (name, storageType, sample value) — family size parameters such as a " +
            "size multiplicity that switches the paper format live here. Pass sheetId of an " +
            "existing sheet to also read the editable INSTANCE parameters of that sheet's " +
            "title block (the values build_system_sheets writes through titleBlockParameters). " +
            "Pass titleBlockTypeId to inspect one type. Also returns existingSheets[] with " +
            "sizes and nextNumberHint — the number that continues the album (build_system_sheets " +
            "auto-numbers the same way). When the project has no title block type the tool " +
            "returns title_block_missing.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetId"":{""type"":""integer"",""description"":""Existing sheet: adds the editable instance parameters of its title block""},""titleBlockTypeId"":{""type"":""integer"",""description"":""Inspect only this title block type; omit for all""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var sheetId = AgentJson.GetLong(args, "sheetId", 0);
        var titleBlockTypeId = AgentJson.GetLong(args, "titleBlockTypeId", 0);

        ViewSheet? sourceSheet = null;
        if (sheetId > 0)
        {
            sourceSheet = doc.GetElement(ElementIdCompat.Create(sheetId)) as ViewSheet;
            if (sourceSheet is null)
            {
                return ToolResult.Fail(
                    "sheet_not_found",
                    "Element " + sheetId + " is not a sheet.");
            }
        }

        var instancesByType = CollectTitleBlockInstances(doc);
        FamilyInstance? sourceInstance = null;
        if (sourceSheet is not null)
        {
            foreach (var instance in instancesByType)
            {
                if (instance.OwnerViewId == sourceSheet.Id)
                {
                    sourceInstance = instance;
                    break;
                }
            }
        }

        var rows = new List<object>();
        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsElementType())
        {
            ct.ThrowIfCancellationRequested();

            if (element is not FamilySymbol symbol)
                continue;

            var typeId = symbol.Id.GetValue();
            if (titleBlockTypeId > 0 && typeId != titleBlockTypeId)
                continue;

            FamilyInstance? sampleInstance = null;
            foreach (var instance in instancesByType)
            {
                if (instance.GetTypeId().GetValue() == typeId)
                {
                    sampleInstance = instance;
                    break;
                }
            }

            var row = new TitleBlockRow
            {
                TitleBlockTypeId = typeId,
                FamilyName = symbol.FamilyName,
                TypeName = symbol.Name,
                SheetCount = CountSheetsOfType(instancesByType, typeId),
                TypeParameters = DescribeEditableParameters(symbol, sampleInstance)
            };

            if (sourceInstance is not null
                && sourceInstance.GetTypeId().GetValue() == typeId)
            {
                row.InstanceParameters = DescribeEditableParameters(sourceInstance, null);
            }

            rows.Add(row.ToPayload());
        }

        if (rows.Count == 0)
        {
            return ToolResult.Fail(
                titleBlockTypeId > 0 ? "titleblock_not_found" : "title_block_missing",
                titleBlockTypeId > 0
                    ? "Element " + titleBlockTypeId + " is not a title block type."
                    : "The project has no loaded title block type; ViewSheet.Create needs one.");
        }

        var scheduleCounts = AgentSheetReader.CountPlacedSchedules(doc);
        var sheets = AgentSheetReader.Collect(doc, excludePlaceholders: false)
            .OrderBy(sheet => sheet.SheetNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var visualDuplicates = AgentSheetReader.FindVisualDuplicateNumbers(sheets);
        var sheetRows = new List<object>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var card = AgentSheetReader.CreateCard(sheet, scheduleCounts, visualDuplicates);
            sheetRows.Add(new
            {
                sheetId = card.SheetId,
                number = card.Number,
                name = card.Name,
                widthMm = card.WidthMm,
                heightMm = card.HeightMm
            });
        }

        var nextNumberHint = AgentSheetReader.NextNumberHint(doc);
        var payload = new
        {
            titleblocks = rows,
            existingSheets = sheetRows,
            existingSheetCount = sheetRows.Count,
            nextNumberHint,
            sourceSheet = sourceSheet is null ? null : new
            {
                sheetId = sourceSheet.Id.GetValue(),
                number = sourceSheet.SheetNumber,
                name = sourceSheet.Name
            }
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            rows.Count + " title block type(s), " + sheetRows.Count +
            " existing sheet(s); next album number: " + nextNumberHint + "." +
            (sourceInstance is null
                ? string.Empty
                : " Instance parameters read from sheet " + sourceSheet!.SheetNumber + "."));
    }

    private static List<FamilyInstance> CollectTitleBlockInstances(Document doc)
    {
        var instances = new List<FamilyInstance>();
        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            if (element is FamilyInstance instance)
                instances.Add(instance);
        }

        return instances;
    }

    private static int CountSheetsOfType(
        List<FamilyInstance> instances,
        long typeId)
    {
        var count = 0;
        foreach (var instance in instances)
        {
            if (instance.GetTypeId().GetValue() == typeId)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Editable parameters as name/storageType/value rows. The value sample
    /// comes from the owner element, falling back to the first placed instance
    /// of the type (a type-level default can read empty until a sheet exists).
    /// </summary>
    private static List<object> DescribeEditableParameters(
        Element owner,
        FamilyInstance? sampleInstance)
    {
        var rows = new List<object>();
        foreach (Parameter parameter in owner.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrEmpty(name) || parameter.IsReadOnly)
                continue;

            var value = Describe(parameter);
            if (string.IsNullOrEmpty(value) && sampleInstance is not null)
            {
                var fallback = FindOnElement(sampleInstance, name!);
                if (fallback is not null)
                    value = Describe(fallback);
            }

            rows.Add(new
            {
                name,
                storageType = parameter.StorageType.ToString(),
                value
            });
        }

        return rows;
    }

    private static Parameter? FindOnElement(Element element, string name)
    {
        var byLookup = element.LookupParameter(name);
        if (byLookup is not null)
            return byLookup;

        var named = element.GetParameters(name);
        return named.Count > 0 ? named[0] : null;
    }

    private static string? Describe(Parameter parameter)
    {
        try
        {
            if (!parameter.HasValue)
                return null;

            return parameter.StorageType switch
            {
                StorageType.String => parameter.AsString(),
                StorageType.Integer => parameter.AsInteger()
                    .ToString(CultureInfo.InvariantCulture),
                StorageType.Double => parameter.AsValueString()
                    ?? parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture),
                StorageType.ElementId => parameter.AsElementId().GetValue()
                    .ToString(CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class TitleBlockRow
    {
        public long TitleBlockTypeId { get; set; }

        public string FamilyName { get; set; } = string.Empty;

        public string TypeName { get; set; } = string.Empty;

        public int SheetCount { get; set; }

        public List<object> TypeParameters { get; set; } = new();

        public List<object>? InstanceParameters { get; set; }

        public object ToPayload() => new
        {
            titleBlockTypeId = TitleBlockTypeId,
            familyName = FamilyName,
            typeName = TypeName,
            sheetCount = SheetCount,
            typeParameters = TypeParameters,
            instanceParameters = InstanceParameters
        };
    }
}
