using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Read-тур обхода физической цепи MEP-системы от элемента (кейс 10):
/// BFS по коннекторам (паттерн ElementChainIterator.BuildGraph из PipeConnect,
/// локально — с лимитом элементов), все свободные коннекторы цепи и детект
/// разрывов: пары свободных коннекторов разных элементов с малым зазором
/// origin→origin (недосынект типа «Т11/Т111»). Read-only.
/// </summary>
public sealed class TraceSystemChainTool : AgentReadToolBase
{
    private const int HardMaxElements = 5000;
    private const int DefaultMaxElements = 500;
    private const double DefaultGapThresholdMm = 300.0;

    /// <summary>Нижняя граница зазора, мм: отсекает шум 1e-6 ft у стыкованных коннекторов.</summary>
    private const double MinGapMm = 0.001;

    public TraceSystemChainTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "trace_system_chain",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Walks the physically connected MEP chain from one element (BFS over pipe/duct " +
            "connectors, the production chain-iterator pattern) and returns the chain with BFS " +
            "levels, element cards (mark, name, category, system name read from each element), " +
            "the visited count and a truncated flag for maxElements. Then it detects BREAKS: " +
            "pairs of free connectors of DIFFERENT elements whose origins are within " +
            "gapThresholdMm of each other (a missed connection — e.g. why one physical branch " +
            "splits into two Revit systems), each break with both element and " +
            "connector ids, the gap in mm and both system names. Also reports systemNameChanges " +
            "along the chain where the system name changes between neighbouring elements. " +
            "The answer also carries startConnectorCount: when the start element has no " +
            "connectors (startConnectorNote says why) the chain cannot extend beyond it and a " +
            "warning is returned — chainLength=1 with 0 breaks is then not a connected run. " +
            "Read-only, no regeneration.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""elementId"":{""type"":""integer""},""maxElements"":{""type"":""integer"",""minimum"":1,""maximum"":5000,""default"":500},""gapThresholdMm"":{""type"":""number"",""minimum"":0.001,""maximum"":10000,""default"":300.0}},""required"":[""elementId""],""additionalProperties"":false}"
    };

    private sealed class ChainNode
    {
        public required ElementId ElementId { get; init; }

        public int BfsLevel { get; init; }
    }

    private sealed class FreeConnector
    {
        public bool IsChainMember;
        public required long ElementId { get; init; }

        public required int ConnectorId { get; init; }

        public required XYZ Origin { get; init; }

        public string? SystemName { get; init; }
    }

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("elementId", out var idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt64(out var elementIdValue))
        {
            return ToolResult.Fail("invalid_arguments", "elementId is required (integer).");
        }

        var maxElements = Math.Min(
            Math.Max(AgentJson.GetInt(args, "maxElements", DefaultMaxElements), 1),
            HardMaxElements);

        var gapThresholdMm = AgentJson.TryGetDouble(args, "gapThresholdMm", out var threshold)
            ? Math.Max(threshold, MinGapMm)
            : DefaultGapThresholdMm;

        var startElement = doc.GetElement(ElementIdCompat.Create(elementIdValue));
        if (startElement is null)
        {
            return ToolResult.Fail(
                "element_not_found",
                "Element " + elementIdValue + " does not exist in this document.");
        }

        // A chain of one element with zero breaks reads as a clean result; when the
        // start element cannot connect to anything, say so instead (C10-02).
        var startConnectorCount = AgentConnectorNotes.CountConnectable(startElement);
        var startNote = startConnectorCount is null or 0
            ? AgentConnectorNotes.DescribeAbsence(startElement)
            : null;
        var warnings = new List<string>();
        if (startNote is not null)
        {
            warnings.Add(
                "The start element has no connectors (" + startNote + "), so the chain cannot " +
                "extend beyond it: chainLength=1 and breaks=[] are NOT a connected, intact run.");
        }

        // 1. BFS по соединённым коннекторам (паттерн ElementChainIterator.BuildGraph).
        var startId = startElement.Id;
        var comparer = ElementIdEqualityComparer.Instance;
        var visited = new HashSet<ElementId>(comparer) { startId };
        var chain = new List<ChainNode> { new() { ElementId = startId, BfsLevel = 0 } };
        var queue = new Queue<ChainNode>();
        queue.Enqueue(chain[0]);
        var truncated = false;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = queue.Dequeue();
            var element = doc.GetElement(node.ElementId);
            var manager = AgentRelationsSupport.GetConnectorManager(element);
            if (manager is null)
                continue;

            foreach (Connector conn in manager.Connectors)
            {
                if (conn.ConnectorType == ConnectorType.Curve) // I-08
                    continue;
                if (!conn.IsConnected)
                    continue;

                foreach (Connector reference in conn.AllRefs)
                {
                    var owner = reference.Owner;
                    if (owner is null || owner is MEPSystem)
                        continue;
                    if (comparer.Equals(owner.Id, node.ElementId))
                        continue;

                    if (visited.Contains(owner.Id))
                        continue;
                    if (chain.Count >= maxElements)
                    {
                        truncated = true;
                        continue;
                    }

                    visited.Add(owner.Id);
                    var next = new ChainNode
                    {
                        ElementId = owner.Id,
                        BfsLevel = node.BfsLevel + 1
                    };
                    chain.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        // 2. Свободные коннекторы всех элементов цепи (паттерн GetChainEndConnectors).
        var freeConnectors = new List<FreeConnector>();
        foreach (var node in chain)
        {
            var element = doc.GetElement(node.ElementId);
            var manager = AgentRelationsSupport.GetConnectorManager(element);
            if (manager is null)
                continue;

            foreach (Connector conn in manager.Connectors)
            {
                if (conn.ConnectorType == ConnectorType.Curve) // I-08
                    continue;
                if (conn.IsConnected)
                    continue;

                freeConnectors.Add(new FreeConnector
                {
                    ElementId = node.ElementId.GetValue(),
                    ConnectorId = conn.Id,
                    Origin = conn.Origin,
                    SystemName = AgentRelationsSupport.ReadSystemName(element!),
                    IsChainMember = true
                });
            }
        }

        // 2а. Внешние свободные коннекторы рядом с концами цепи: разрыв по
        // определению ведёт к элементу ВНЕ цепи (BFS его не пересёк), поэтому
        // для каждого свободного конца ищем MEP-элементы в окне gapThreshold
        // и собираем их свободные коннекторы — они образуют вторую сторону
        // разрыва и дают toSystemName (механика «Т11/Т111»).
        CollectExternalFreeConnectors(doc, freeConnectors, visited, gapThresholdMm);

        // 3. Разрывы: ближние пары свободных коннекторов разных элементов.
        // Кандидаты сортируются по дистанции и жадно спариваются — каждый
        // коннектор попадает максимум в один разрыв (ближний приоритетнее).
        var breaks = FindBreaks(freeConnectors, gapThresholdMm);

        // 4. Смены имени системы вдоль цепи (BFS-порядок обхода).
        var systemNameChanges = new List<object>();
        string? previous = null;
        var first = true;
        foreach (var node in chain)
        {
            var element = doc.GetElement(node.ElementId);
            var systemName = element is null ? null : AgentRelationsSupport.ReadSystemName(element);
            if (first)
            {
                previous = systemName;
                first = false;
                continue;
            }

            if (!string.Equals(systemName, previous, StringComparison.Ordinal))
            {
                systemNameChanges.Add(new
                {
                    elementId = node.ElementId.GetValue(),
                    before = previous,
                    after = systemName
                });
                previous = systemName;
            }
        }

        var payload = new
        {
            startElementId = startId.GetValue(),
            startConnectorCount,
            startConnectorNote = startNote,
            chain = chain.Select(node =>
            {
                var element = doc.GetElement(node.ElementId);
                return new
                {
                    elementId = node.ElementId.GetValue(),
                    mark = element is null ? null : AgentRelationsSupport.ReadMark(element),
                    name = element?.Name,
                    category = element is null
                        ? null
                        : AgentElementCardFactory.GetCategoryName(element.Category),
                    systemName = element is null ? null : AgentRelationsSupport.ReadSystemName(element),
                    bfsLevel = node.BfsLevel
                };
            }).ToArray(),
            chainLength = chain.Count,
            breaks,
            systemNameChanges,
            visitedCount = visited.Count,
            truncated
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Traced chain of " + chain.Count + " element(s), "
            + breaks.Count + " break(s), "
            + systemNameChanges.Count + " system name change(s)."
            + (startNote is null
                ? string.Empty
                : " WARNING: the start element has no connectors (" + startNote +
                  ") — this is not a connected run."),
            warnings: warnings);
    }

    /// <summary>
    /// Внешние свободные коннекторы возле концов цепи: разрыв ведёт к элементу
    /// вне BFS-цепи, поэтому кандидаты ищутся bbox-коллектором в окне
    /// gapThreshold вокруг каждого свободного конца цепи (MEPCurve или
    /// FamilyInstance с коннекторами). Дубли исключаются по elementId.
    /// </summary>
    private static void CollectExternalFreeConnectors(
        Document doc,
        List<FreeConnector> freeConnectors,
        HashSet<ElementId> chainElements,
        double gapThresholdMm)
    {
        var knownKeys = new HashSet<(long ElementId, int ConnectorId)>(
            freeConnectors.Select(c => (c.ElementId, c.ConnectorId)));
        var knownFreeOrigins = freeConnectors.Select(c => c.Origin).ToList();
        var radius = gapThresholdMm / SmartCon.Core.Units.FeetToMm;
        var filter = new LogicalOrFilter(
            new ElementClassFilter(typeof(MEPCurve)),
            new ElementClassFilter(typeof(FamilyInstance)));

        foreach (var origin in knownFreeOrigins)
        {
            var min = origin - new XYZ(radius, radius, radius);
            var max = origin + new XYZ(radius, radius, radius);
            var outline = new Outline(min, max);

            foreach (var element in new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .WherePasses(new BoundingBoxIntersectsFilter(outline)))
            {
                if (chainElements.Contains(element.Id))
                    continue;

                var manager = AgentRelationsSupport.GetConnectorManager(element);
                if (manager is null)
                    continue;

                foreach (Connector conn in manager.Connectors)
                {
                    if (conn.ConnectorType == ConnectorType.Curve) // I-08
                        continue;
                    if (conn.IsConnected)
                        continue;

                    var key = (element.Id.GetValue(), conn.Id);
                    if (!knownKeys.Add(key))
                        continue;

                    freeConnectors.Add(new FreeConnector
                    {
                        ElementId = element.Id.GetValue(),
                        ConnectorId = conn.Id,
                        Origin = conn.Origin,
                        SystemName = AgentRelationsSupport.ReadSystemName(element)
                    });
                }
            }
        }
    }

    private static List<object> FindBreaks(List<FreeConnector> freeConnectors, double gapThresholdMm)
    {
        var candidates = new List<(FreeConnector From, FreeConnector To, double GapMm)>();
        for (var i = 0; i < freeConnectors.Count; i++)
        {
            for (var j = i + 1; j < freeConnectors.Count; j++)
            {
                var first = freeConnectors[i];
                var second = freeConnectors[j];
                if (first.ElementId == second.ElementId)
                    continue;

                // Разрыв цепи обязан касаться её элемента: пара двух посторонних
                // коннекторов, попавших в одно окно, — шум, не разрыв трассы.
                if (!first.IsChainMember && !second.IsChainMember)
                    continue;

                var gapMm = first.Origin.DistanceTo(second.Origin) * SmartCon.Core.Units.FeetToMm;
                if (gapMm <= MinGapMm || gapMm > gapThresholdMm)
                    continue;

                // Детерминированный порядок сторон: меньший elementId — from.
                var (from, to) = first.ElementId < second.ElementId ? (first, second) : (second, first);
                candidates.Add((from, to, gapMm));
            }
        }

        candidates.Sort((a, b) => a.GapMm.CompareTo(b.GapMm));

        var usedConnectors = new HashSet<(long ElementId, int ConnectorId)>();
        var breaks = new List<object>();
        foreach (var (from, to, gapMm) in candidates)
        {
            if (usedConnectors.Contains((from.ElementId, from.ConnectorId))
                || usedConnectors.Contains((to.ElementId, to.ConnectorId)))
                continue;

            usedConnectors.Add((from.ElementId, from.ConnectorId));
            usedConnectors.Add((to.ElementId, to.ConnectorId));
            breaks.Add(new
            {
                fromElementId = from.ElementId,
                fromConnectorId = from.ConnectorId,
                toElementId = to.ElementId,
                toConnectorId = to.ConnectorId,
                gapMm,
                fromSystemName = from.SystemName,
                toSystemName = to.SystemName
            });
        }

        return breaks;
    }
}
