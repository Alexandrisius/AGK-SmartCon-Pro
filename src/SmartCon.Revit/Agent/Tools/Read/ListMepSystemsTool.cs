using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the logical MEP systems (MEPSystem instances: piping, mechanical/duct,
/// electrical circuits). The abbreviation and the system classification live on
/// the MEPSystemType, so the card carries the typeId for scope="type" writes.
/// </summary>
public sealed class ListMepSystemsTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public ListMepSystemsTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_mep_systems",
        Description =
            "Lists MEP systems (инженерные системы): piping, mechanical (duct) and electrical " +
            "circuits, without opening anything. Each row: systemId, name, domain " +
            "(piping|mechanical|electrical), classification (MEPSystemClassification name like " +
            "SupplyHydronic or SupplyAir, null for electrical circuits), typeName/typeId of the " +
            "system type, its abbreviation, elementCount, equipmentCount " +
            "(0 or 1 base equipment) and isEmpty. elementCount is the number of model elements " +
            "assigned to the system (pipes, fittings, accessories, terminals, fixtures, " +
            "equipment): the union of the system's terminal set, its base equipment and its " +
            "pipe/duct network — MEPSystem.Elements alone holds only the terminals. " +
            "Filters: nameContains, domain, classification. " +
            "Paged: limit (default 200, max 500), offset, returned, nextOffset, truncated. " +
            "fields=[\"systemId\",\"name\",\"abbreviation\"] projects each card onto the listed " +
            "properties; unknown names are reported in ignoredFields with availableFields. " +
            "An empty list is a valid answer (a project without MEP).",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""nameContains"":{""type"":""string""},""domain"":{""type"":""string"",""enum"":[""piping"",""mechanical"",""electrical""]},""classification"":{""type"":""string"",""description"":""MEPSystemClassification name, e.g. SupplyHydronic, DomesticColdWater, Sanitary, SupplyAir, ReturnAir, PowerCircuit""},""fields"":{""type"":""array"",""description"":""Allowed: systemId,id,name,uniqueId,domain,classification,typeName,typeId,abbreviation,elementCount,equipmentCount,isEmpty. Returned keys match the requested names."",""items"":{""type"":""string""}},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var nameContains = AgentJson.GetString(args, "nameContains");
        var domain = AgentJson.GetString(args, "domain");
        var classification = AgentJson.GetString(args, "classification");
        var fields = AgentJson.GetStringArray(args, "fields");
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit), DefaultLimit, MaxLimit);

        if (domain is not null
            && !string.Equals(domain, "piping", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(domain, "mechanical", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(domain, "electrical", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Fail(
                "unknown_domain",
                "Unknown domain '" + domain + "'. Allowed: piping, mechanical, electrical. " +
                "[Action: drop the domain filter or use one of the allowed values]");
        }

        var matched = new List<MepSystemCard>();
        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(MEPSystem))
                     .WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();
            if (element is not MEPSystem system)
                continue;

            var card = CreateCard(doc, system);
            if (!Contains(card.Name, nameContains))
                continue;
            if (domain is not null
                && !string.Equals(card.Domain, domain, StringComparison.OrdinalIgnoreCase))
                continue;
            if (classification is not null
                && (card.Classification is null
                    || !string.Equals(card.Classification, classification, StringComparison.OrdinalIgnoreCase)))
                continue;

            matched.Add(card);
        }

        matched.Sort((left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.SystemId.CompareTo(right.SystemId);
        });

        var total = matched.Count;
        var page = AgentPaging.Slice(matched, offset, limit);
        var hasMore = offset + page.Count < total;

        if (fields.Count > 0)
        {
            var projected = new List<JsonElement>(page.Count);
            var ignored = new List<string>();
            var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in page)
            {
                projected.Add(AgentCardProjection.Apply(
                    AgentJson.SerializeCard(card), fields, ignored, substituted));
            }

            var available = page.Count > 0
                ? AgentCardProjection.AvailableFields(AgentJson.SerializeCard(page[0]))
                : Array.Empty<string>();

            var projectedPayload = new
            {
                total,
                offset,
                returned = projected.Count,
                nextOffset = hasMore ? offset + projected.Count : (int?)null,
                truncated = hasMore,
                ignoredFields = ignored.Count > 0 ? ignored : null,
                substitutedFields = substituted.Count > 0 ? substituted : null,
                availableFields = ignored.Count > 0 ? available : null,
                systems = projected
            };

            return ToolResult.Ok(
                AgentJson.Serialize(projectedPayload),
                Message(total, projected.Count, hasMore));
        }

        var payload = new
        {
            total,
            offset,
            returned = page.Count,
            nextOffset = hasMore ? offset + page.Count : (int?)null,
            truncated = hasMore,
            systems = page
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            Message(total, page.Count, hasMore));
    }

    private static string Message(int total, int returned, bool hasMore) =>
        "Matched " + total + " MEP system(s); returned " + returned + "." +
        (hasMore
            ? " The result is truncated — pass nextOffset as offset to read the next page."
            : string.Empty);

    private static MepSystemCard CreateCard(Document doc, MEPSystem system)
    {
        var typeId = system.GetTypeId();
        var type = typeId is not null && typeId != ElementId.InvalidElementId
            ? doc.GetElement(typeId) as MEPSystemType
            : null;

        return new MepSystemCard
        {
            SystemId = system.Id.GetValue(),
            UniqueId = system.UniqueId,
            Name = system.Name ?? string.Empty,
            Domain = DomainOf(system),
            Classification = type?.SystemClassification.ToString(),
            TypeName = type?.Name,
            TypeId = type?.Id.GetValue(),
            Abbreviation = type?.Abbreviation,

            // N-ds-7: MEPSystem.Elements holds only the terminal elements and
            // excludes the base equipment; the pipes/ducts+fittings live in
            // PipingNetwork/DuctNetwork. The union is the physical system.
            ElementCount = CountMembers(system),
            EquipmentCount = system.BaseEquipment is null ? 0 : 1,
            IsEmpty = system.IsEmpty
        };
    }

    private static int CountMembers(MEPSystem system)
    {
        // ElementSet iterates ELEMENTS, not ids (proven by an integration-test
        // InvalidCastException: "Unable to cast 'Pipe' to 'ElementId'") — the
        // same proven union SetParametersJobAction uses.
        var ids = new HashSet<long>();
        if (system.Elements is not null)
            foreach (var member in system.Elements)
                if (member is Element element)
                    ids.Add(element.Id.GetValue());
        if (system.BaseEquipment is { } equipment)
            ids.Add(equipment.Id.GetValue());
        if (system is PipingSystem piping && piping.PipingNetwork is not null)
            foreach (var member in piping.PipingNetwork)
                if (member is Element element)
                    ids.Add(element.Id.GetValue());
        if (system is MechanicalSystem mechanical && mechanical.DuctNetwork is not null)
            foreach (var member in mechanical.DuctNetwork)
                if (member is Element element)
                    ids.Add(element.Id.GetValue());

        return ids.Count;
    }

    private static string DomainOf(MEPSystem system)
    {
        if (system is PipingSystem)
            return "piping";
        if (system is MechanicalSystem)
            return "mechanical";
        if (system is ElectricalSystem)
            return "electrical";
        return "other";
    }

    private static bool Contains(string? value, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;
        if (string.IsNullOrEmpty(value))
            return false;

#if NET8_0_OR_GREATER
        return value.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return value!.IndexOf(needle!, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private sealed class MepSystemCard
    {
        public long SystemId { get; set; }

        public string UniqueId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Domain { get; set; } = string.Empty;

        public string? Classification { get; set; }

        public string? TypeName { get; set; }

        public long? TypeId { get; set; }

        public string? Abbreviation { get; set; }

        public int ElementCount { get; set; }

        public int EquipmentCount { get; set; }

        public bool IsEmpty { get; set; }
    }
}
