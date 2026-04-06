using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Хранилище snapshot-ов элементов цепочки.
/// Живёт в ViewModel как мутабельное поле (не в immutable контексте).
/// Snapshot сохраняется ДО операции +, используется при − для отката.
/// Также отслеживает reducer-ы, вставленные при +, для удаления при −.
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
    /// Запомнить reducer, вставленный при + для элемента.
    /// Reducer удаляется при − (DecrementChainDepth).
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
