using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Virtual CTC assignment store (ADR-002).
/// CTC values are held in memory; writing to family (LoadFamily) is deferred until Connect().
/// Key: (ElementId.Value, ConnectorIndex).
/// </summary>
public sealed class VirtualCtcStore
{
    private readonly Dictionary<(long ElemId, int ConnIdx), ConnectionTypeCode> _overrides = new();
    private readonly Dictionary<(long ElemId, int ConnIdx), ConnectorTypeDefinition> _pendingWrites = new();

    /// <summary>
    /// Set a virtual CTC for a connector.
    /// <paramref name="typeDef"/> — full ConnectorTypeDefinition for deferred write to family.
    /// If typeDef == null, override is created without pending write (already written or MEPCurve).
    /// </summary>
    public void Set(ElementId elemId, int connIdx, ConnectionTypeCode ctc, ConnectorTypeDefinition? typeDef = null)
    {
        var key = (elemId.Value, connIdx);
        _overrides[key] = ctc;
        if (typeDef is not null)
            _pendingWrites[key] = typeDef;
    }

    /// <summary>Get virtual CTC or null if not set.</summary>
    public ConnectionTypeCode? Get(ElementId elemId, int connIdx)
    {
        return _overrides.TryGetValue((elemId.Value, connIdx), out var ctc) ? ctc : null;
    }

    /// <summary>
    /// All virtual CTCs for an element, indexed by ConnectorIndex.
    /// For passing to AlignFittingToStatic as ctcOverrides.
    /// </summary>
    public IReadOnlyDictionary<int, ConnectionTypeCode> GetOverridesForElement(ElementId elemId)
    {
        var result = new Dictionary<int, ConnectionTypeCode>();
        var idVal = elemId.Value;
        foreach (var kvp in _overrides)
        {
            if (kvp.Key.ElemId == idVal)
                result[kvp.Key.ConnIdx] = kvp.Value;
        }
        return result;
    }

    /// <summary>
    /// All pending CTC writes (for FlushVirtualCtcToFamilies in Connect()).
    /// </summary>
    public IReadOnlyList<(ElementId ElementId, int ConnectorIndex, ConnectorTypeDefinition TypeDef)> GetPendingWrites()
    {
        return _pendingWrites
            .Select(kvp => (new ElementId(kvp.Key.ElemId), kvp.Key.ConnIdx, kvp.Value))
            .ToList();
    }

    /// <summary>Whether there is at least one pending write.</summary>
    public bool HasPendingWrites => _pendingWrites.Count > 0;

    /// <summary>Clear all overrides and pending writes.</summary>
    public void Clear()
    {
        _overrides.Clear();
        _pendingWrites.Clear();
    }

    /// <summary>
    /// Remove all overrides and pending writes for the specified ElementId.
    /// Call when deleting a fitting (delete + insert = new ElementId).
    /// </summary>
    public void RemoveForElement(ElementId elemId)
    {
        long val = elemId.Value;
        _overrides.Keys.Where(k => k.ElemId == val).ToList()
            .ForEach(k => _overrides.Remove(k));
        _pendingWrites.Keys.Where(k => k.ElemId == val).ToList()
            .ForEach(k => _pendingWrites.Remove(k));
    }

    /// <summary>
    /// Remove all pending writes (leaving overrides for GetEffectiveConnectorCtc).
    /// Call after successful FlushVirtualCtcToFamilies.
    /// </summary>
    public void ClearPendingWrites()
    {
        _pendingWrites.Clear();
    }

    /// <summary>
    /// Transfer all overrides and pending writes from old ElementId to new ElementId.
    /// Used when re-inserting a fitting (delete + insert = new ElementId).
    /// Old records are deleted.
    /// </summary>
    public void TransferOverrides(ElementId oldElemId, ElementId newElemId)
    {
        long oldVal = oldElemId.Value;
        long newVal = newElemId.Value;

        var keysToMove = _overrides.Keys.Where(k => k.ElemId == oldVal).ToList();
        foreach (var key in keysToMove)
        {
            var newKey = (newVal, key.ConnIdx);
            _overrides[newKey] = _overrides[key];
            _overrides.Remove(key);
        }

        var pendingToMove = _pendingWrites.Keys.Where(k => k.ElemId == oldVal).ToList();
        foreach (var key in pendingToMove)
        {
            var newKey = (newVal, key.ConnIdx);
            _pendingWrites[newKey] = _pendingWrites[key];
            _pendingWrites.Remove(key);
        }
    }

    // ── Internal long-overloads for unit tests (without RevitAPI) ──────────────────

    internal void Set(long elemIdValue, int connIdx, ConnectionTypeCode ctc, ConnectorTypeDefinition? typeDef = null)
    {
        var key = (elemIdValue, connIdx);
        _overrides[key] = ctc;
        if (typeDef is not null)
            _pendingWrites[key] = typeDef;
    }

    internal ConnectionTypeCode? Get(long elemIdValue, int connIdx)
    {
        return _overrides.TryGetValue((elemIdValue, connIdx), out var ctc) ? ctc : null;
    }

    internal IReadOnlyDictionary<int, ConnectionTypeCode> GetOverridesForElementByIdValue(long elemIdValue)
    {
        var result = new Dictionary<int, ConnectionTypeCode>();
        foreach (var kvp in _overrides)
        {
            if (kvp.Key.ElemId == elemIdValue)
                result[kvp.Key.ConnIdx] = kvp.Value;
        }
        return result;
    }
}
