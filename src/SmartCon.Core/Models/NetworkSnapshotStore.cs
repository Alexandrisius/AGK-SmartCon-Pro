using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Storage of element snapshots for the chain.
/// Lives in ViewModel as a mutable field (not in immutable context).
/// Snapshot is saved BEFORE the plus operation, used on minus for rollback.
/// Also tracks reducers inserted on plus for deletion on minus.
/// </summary>
public sealed class NetworkSnapshotStore
{
    private readonly Dictionary<long, ElementSnapshot> _snapshots = new();
    private readonly Dictionary<long, List<ElementId>> _reducers = new();

    public void Save(ElementSnapshot snapshot)
        => _snapshots[snapshot.ElementId.Value] = snapshot;

    public ElementSnapshot? Get(ElementId elementId)
        => _snapshots.TryGetValue(elementId.Value, out var s) ? s : null;

    /// <summary>
    /// Track a reducer inserted on plus for an element.
    /// Reducer is deleted on minus (DecrementChainDepth).
    /// </summary>
    public void TrackReducer(ElementId elementId, ElementId reducerId)
    {
        var key = elementId.Value;
        if (!_reducers.TryGetValue(key, out var list))
        {
            list = [];
            _reducers[key] = list;
        }
        list.Add(reducerId);
    }

    public IReadOnlyList<ElementId> GetReducers(ElementId elementId)
        => _reducers.TryGetValue(elementId.Value, out var list) ? list : [];
}
