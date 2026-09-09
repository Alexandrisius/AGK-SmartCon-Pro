using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

#pragma warning disable CS9113
/// <summary>
/// Handles element-wise attach/detach of chain elements in the PipeConnect editor.
/// AttachSingleElement: snapshot, disconnect, resize, align, reconnect one element.
/// DetachSingleElement: rollback one element to its snapshot state, delete inserted reducers.
/// </summary>
public sealed partial class ChainOperationHandler(
    IConnectorService connSvc,
    ITransformService transformSvc,
    IParameterResolver paramResolver,
    IFittingInsertService fittingInsertSvc,
    INetworkMover networkMover,
    IAlignmentService alignmentSvc,
    IDynamicSizeResolver sizeResolver)
{
#pragma warning restore CS9113
    /// <summary>Describes the edge from an element to its parent in the chain graph.</summary>
    public record struct ParentEdge(ElementId ParentId, int ParentConnIdx, int ElemConnIdx);

    /// <summary>
    /// Maximum connector gap (mm) for cross-edge restoration: beyond this the loop
    /// did not close after alignment and ConnectTo could drag elements silently.
    /// </summary>
    private const double CrossEdgeMaxGapMm = 2.0;

    /// <summary>Result of attaching a single chain element (element-wise chain mode).</summary>
    /// <param name="DidWork">
    /// True when the element actually changed (moved, rotated, resized, reducer
    /// inserted, or absorbed by pipe length). False = idle element — only
    /// disconnect/reconnect churn.
    /// </param>
    /// <param name="Edge">
    /// The parent edge used for the reconnect (parent connector ↔ element connector),
    /// or null when no parent edge was found and the element was skipped.
    /// </param>
    public readonly record struct SingleElementResult(bool DidWork, ParentEdge? Edge);

    /// <summary>
    /// Attach a single chain element (element-wise chain mode): snapshot, disconnect,
    /// resize, align, reconnect to its parent. Inserts a reducer when radius
    /// adjustment fails. The element's parent must already be attached — the
    /// flattened queue order (see <see cref="ConnectionGraph.GetElementQueue"/>)
    /// guarantees this.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="groupSession">Active transaction group session.</param>
    /// <param name="graph">Chain graph with element levels.</param>
    /// <param name="snapshotStore">Store for element snapshots (for rollback).</param>
    /// <param name="warmedElementIds">Set of already-warmed element IDs.</param>
    /// <param name="entry">Queue entry: element + its BFS level.</param>
    /// <param name="attachedElementIds">
    /// Element ids (numeric) already attached in this session (root + queue[1..current]).
    /// Used to restore cross-edges (network loops): a loop connection to an already
    /// attached neighbor is re-established right after the parent reconnect;
    /// loop neighbors attached later restore the same edge from their own attach.
    /// </param>
    /// <param name="lockNetwork">
    /// User's "Блокировать" toggle: attach the element strictly as-is — no size
    /// adjustment (no transition/reducer/resize) and no pipe-length absorption.
    /// The element is only aligned rigidly to its parent and reconnected.
    /// </param>
    public SingleElementResult AttachSingleElement(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        HashSet<long> warmedElementIds,
        ChainQueueEntry entry,
        IReadOnlyCollection<long> attachedElementIds,
        bool lockNetwork = false)
    {
        using var _scope = SmartConLogger.BeginScope("Chain+",
            ("Method", "AttachSingleElement"),
            ("ElementId", entry.ElementId.GetValue()),
            ("Level", entry.Level),
            ("Lock", lockNetwork));

        var singleElement = new[] { entry.ElementId };

        WarmDepsForLevel(doc, singleElement, warmedElementIds);
        SaveSnapshotForLevelElement(doc, entry.ElementId, graph, snapshotStore);

        var transitionOptions = lockNetwork
            ? new Dictionary<long, IReadOnlyList<FamilySizeOption>>()
            : PrefetchTransitionOptions(doc, graph, entry.Level, singleElement);

        var result = new SingleElementResult(false, null);
        groupSession.RunInTransaction(
            string.Format(LocalizationService.GetString("Tx_ChainElement"), entry.ElementId.GetValue()), doc =>
        {
            bool didWork = ProcessIncrementElement(
                doc, graph, snapshotStore, entry.Level, entry.ElementId,
                elemIndex: 1, levelCount: 1, transitionOptions, attachedElementIds, lockNetwork, out var edge);

            result = new SingleElementResult(didWork, edge);
            doc.Regenerate();
        });

        SmartConLogger.Debug($"═══ ELEMENT {entry.ElementId.GetValue()} DONE ═══ (didWork={result.DidWork})");
        return result;
    }

    /// <summary>
    /// Pre-fetch available size configurations for FamilyInstance elements that
    /// mismatch their parent radius. Must run OUTSIDE a transaction
    /// (GetAvailableFamilySizes uses EditFamily, which requires IsModifiable == false).
    /// Used by the TRANSITION strategy inside the level transaction (ADR-053).
    /// </summary>
    private Dictionary<long, IReadOnlyList<FamilySizeOption>> PrefetchTransitionOptions(
        Document doc,
        ConnectionGraph graph,
        int nextLevel,
        IReadOnlyList<ElementId> levelElements)
    {
        var result = new Dictionary<long, IReadOnlyList<FamilySizeOption>>();
        foreach (var elemId in levelElements)
        {
            if (doc.GetElement(elemId) is not FamilyInstance)
                continue;

            var edge = FindEdgeToParent(elemId, nextLevel, graph);
            if (edge is null)
                continue;

            var parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
            var elemProxy = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
            if (parentProxy is null || elemProxy is null)
                continue;

            if (System.Math.Abs(parentProxy.Radius - elemProxy.Radius) <= 1e-5)
                continue;

            try
            {
                var options = sizeResolver.GetAvailableFamilySizes(doc, elemId, edge.Value.ElemConnIdx);
                if (options.Count > 0)
                {
                    result[elemId.GetValue()] = options;
                    SmartConLogger.Debug($"  Prefetch: elemId={elemId.GetValue()}, {options.Count} size options");
                }
            }
            catch (Exception exPrefetch)
            {
                SmartConLogger.Warn($"  Prefetch sizes failed for {elemId.GetValue()}: {exPrefetch.Message} " +
                    $"[Action: переходная конфигурация недоступна, будет классический resize]");
            }
        }
        return result;
    }

    /// <summary>
    /// Detach a single chain element (element-wise chain mode): restore it to its
    /// snapshot state, delete inserted reducers, and reconnect original connections.
    /// Elements must be detached in strict LIFO order (queue tail first) so the
    /// parent edge is still intact when a child is rolled back.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="groupSession">Active transaction group session.</param>
    /// <param name="graph">Chain graph.</param>
    /// <param name="snapshotStore">Snapshot store with saved element states.</param>
    /// <param name="entry">Queue entry: element + its BFS level.</param>
    public void DetachSingleElement(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        ChainQueueEntry entry)
    {
        using var _scope = SmartConLogger.BeginScope("Chain-",
            ("Method", "DetachSingleElement"),
            ("ElementId", entry.ElementId.GetValue()),
            ("Level", entry.Level));

        groupSession.RunInTransaction(
            string.Format(LocalizationService.GetString("Tx_ChainRollbackElement"), entry.ElementId.GetValue()), doc =>
        {
            RollbackElement(doc, graph, snapshotStore, entry.Level, entry.ElementId);
            doc.Regenerate();
        });
    }

    /// <summary>
    /// Try to "seal" the chain early (ADR-052): when the displacement is already
    /// fully absorbed and the remaining downstream needs no resize work, the
    /// boundary edges (currentDepth → currentDepth+1) are reconnected and the
    /// rest of the network is left completely untouched — no per-element churn.
    /// Returns true when the chain is sealed (caller disables further increments).
    /// A level is "quiet" when every boundary child has zero alignment offset,
    /// no required rotation and a matching radius, and every deeper piping edge
    /// has matching radii (a resize deeper would require classic processing).
    /// On partial ConnectTo failure every edge connected so far is disconnected
    /// again inside the same transaction — a failed seal never leaves the
    /// network half-sealed.
    /// </summary>
    /// <param name="sealedEdges">
    /// On success: the boundary edges connected by this seal (caller stores them
    /// so the seal can be torn down later — see UnsealIfSealed in the editor VM).
    /// Empty on failure.
    /// </param>
    public bool TrySealQuietChain(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        int currentDepth,
        out IReadOnlyList<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)> sealedEdges)
    {
        sealedEdges = [];
        if (currentDepth + 1 >= graph.Levels.Count)
            return true;

        // Per-call connector cache: quiet checks read every deeper edge once —
        // without caching each RefreshConnector re-walks the ConnectorManager.
        var proxyCache = new Dictionary<(long, int), ConnectorProxy?>();
        ConnectorProxy? RefreshCached(ElementId id, int idx)
        {
            var key = (id.GetValue(), idx);
            if (!proxyCache.TryGetValue(key, out var proxy))
            {
                proxy = connSvc.RefreshConnector(doc, id, idx);
                proxyCache[key] = proxy;
            }
            return proxy;
        }

        var levelOf = new Dictionary<long, int>();
        for (int level = 0; level < graph.Levels.Count; level++)
            foreach (var id in graph.Levels[level])
                levelOf[id.GetValue()] = level;

        var boundaryEdges = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();

        foreach (var childId in graph.Levels[currentDepth + 1])
        {
            var edge = FindEdgeToParent(childId, currentDepth + 1, graph);
            if (edge is null)
                return false;

            var parentProxy = RefreshCached(edge.Value.ParentId, edge.Value.ParentConnIdx);
            var childProxy = RefreshCached(childId, edge.Value.ElemConnIdx);
            if (parentProxy is null || childProxy is null)
                continue;

            if (parentProxy.Domain != Domain.DomainPiping || childProxy.Domain != Domain.DomainPiping)
                continue;

            double radiusDelta = System.Math.Abs(parentProxy.Radius - childProxy.Radius);
            if (radiusDelta > 1e-5)
                return false;

            var alignResult = ConnectorAligner.ComputeAlignment(
                parentProxy.OriginVec3, parentProxy.BasisZVec3, parentProxy.BasisXVec3,
                childProxy.OriginVec3, childProxy.BasisZVec3, childProxy.BasisXVec3);

            if (!VectorUtils.IsZero(alignResult.InitialOffset)
                || alignResult.BasisZRotation is not null
                || alignResult.BasisXSnap is not null)
                return false;

            boundaryEdges.Add((edge.Value.ParentId, edge.Value.ParentConnIdx, childId, edge.Value.ElemConnIdx));
        }

        foreach (var edge in graph.Edges)
        {
            if (!levelOf.TryGetValue(edge.FromElementId.GetValue(), out int fromLevel)
                || !levelOf.TryGetValue(edge.ToElementId.GetValue(), out int toLevel)
                || fromLevel <= currentDepth
                || toLevel <= currentDepth)
                continue;

            var fromProxy = RefreshCached(edge.FromElementId, edge.FromConnectorIndex);
            var toProxy = RefreshCached(edge.ToElementId, edge.ToConnectorIndex);
            if (fromProxy is null || toProxy is null)
                continue;

            if (fromProxy.Domain != Domain.DomainPiping || toProxy.Domain != Domain.DomainPiping)
                continue;

            double radiusDelta = System.Math.Abs(fromProxy.Radius - toProxy.Radius);
            if (radiusDelta > 1e-5)
            {
                SmartConLogger.Debug($"Seal: radius mismatch at deeper edge " +
                    $"{edge.FromElementId.GetValue()}↔{edge.ToElementId.GetValue()} " +
                    $"({radiusDelta * FeetToMm:F2}mm) → classic flow");
                return false;
            }
        }

        bool allConnected = true;
        var sealedCrossEdges = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();
        groupSession.RunInTransaction(LocalizationService.GetString("Tx_ChainSeal"), doc =>
        {
            var connected = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();
            foreach (var (parentId, parentConnIdx, childId, childConnIdx) in boundaryEdges)
            {
                try
                {
                    connSvc.ConnectTo(doc, parentId, parentConnIdx, childId, childConnIdx);
                    connected.Add((parentId, parentConnIdx, childId, childConnIdx));
                }
                catch (Exception exConn)
                {
                    allConnected = false;
                    SmartConLogger.Warn($"Seal: ConnectTo {parentId.GetValue()}↔{childId.GetValue()} " +
                        $"failed: {exConn.Message} [Action: граница будет подключена поэлементным обходом]");
                    break;
                }
            }

            if (!allConnected)
            {
                // Roll back the partially sealed boundary — a failed seal must
                // never leave the network half-sealed (caller falls back to
                // element-wise traversal with visible per-element errors).
                foreach (var (_, _, childId, childConnIdx) in connected)
                {
                    try { connSvc.DisconnectAllFromConnector(doc, childId, childConnIdx); }
                    catch (Exception exDisc)
                    {
                        SmartConLogger.Warn($"Seal rollback: DisconnectAllFromConnector {childId.GetValue()}:{childConnIdx} " +
                            $"failed: {exDisc.Message} [Action: граница может остаться частично подключённой — проверьте соединения]");
                    }
                }
                connected.Clear();
            }
            else
            {
                // Restore cross-edges (network loops) between the sealed boundary
                // and the attached part: they were torn by the classic attach of
                // the attached element, and the sealed child never passes through
                // AttachSingleElement — without this the loop stays torn (bug:
                // pipe left detached from a tee after seal compensation).
                RestoreCrossEdgesForBoundary(doc, graph, currentDepth, levelOf, sealedCrossEdges);
            }
            doc.Regenerate();
        });

        if (!allConnected)
        {
            SmartConLogger.Warn("Seal: some boundary edges failed — falling back to element-wise traversal " +
                "[Action: продолжайте подключение кнопкой «+» или «Подключить всё» — сбойный элемент покажет ошибку]");
            return false;
        }

        sealedEdges = boundaryEdges.Concat(sealedCrossEdges).ToList();
        SmartConLogger.Info($"Seal: chain sealed at level {currentDepth}, " +
            $"boundary edges={boundaryEdges.Count}, cross-edges restored={sealedCrossEdges.Count}, deeper levels untouched");
        return true;
    }

    private void LogIncrementElementHeader(Document doc, ElementId elemId, int elemIndex, int levelCount)
    {
        var elemRaw = doc.GetElement(elemId);
        string elemName = elemRaw?.Name ?? "?";
        string elemType = elemRaw?.GetType().Name ?? "?";
        SmartConLogger.Debug($"  ── Element {elemIndex}/{levelCount}: " +
            $"id={elemId.GetValue()} '{elemName}' ({elemType}) ──");
    }

    private void DisconnectElementConnections(Document doc, ElementId elemId)
    {
        var allConns = connSvc.GetAllConnectors(doc, elemId);
        int disconnected = 0;
        foreach (var c in allConns)
        {
            if (!c.IsFree)
            {
                SmartConLogger.Debug($"a. Disconnect connIdx={c.ConnectorIndex} " +
                    $"(R={c.Radius * FeetToMm:F2}mm)");

                connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex);
                disconnected++;
            }
        }

        SmartConLogger.Debug($"a. Disconnect done: {disconnected} connections broken, всего коннекторов={allConns.Count}");
    }

    /// <summary>Find the edge connecting an element to its parent in the previous BFS level.</summary>
    public static ParentEdge? FindEdgeToParent(ElementId elemId, int level, ConnectionGraph graph)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        var parentLevel = graph.Levels[level - 1];
        var parentIds = new HashSet<ElementId>(parentLevel, comparer);

        foreach (var edge in graph.Edges)
        {
            if (comparer.Equals(edge.ToElementId, elemId) && parentIds.Contains(edge.FromElementId))
                return new ParentEdge(edge.FromElementId, edge.FromConnectorIndex, edge.ToConnectorIndex);
            if (comparer.Equals(edge.FromElementId, elemId) && parentIds.Contains(edge.ToElementId))
                return new ParentEdge(edge.ToElementId, edge.ToConnectorIndex, edge.FromConnectorIndex);
        }
        return null;
    }

    /// <summary>Check whether an element belongs to the current chain (up to maxLevel).</summary>
    public static bool IsInCurrentChain(ElementId elemId, int maxLevel, ConnectionGraph graph)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        for (int level = 0; level <= maxLevel && level < graph.Levels.Count; level++)
        {
            foreach (var id in graph.Levels[level])
            {
                if (comparer.Equals(id, elemId))
                    return true;
            }
        }
        return false;
    }
}
