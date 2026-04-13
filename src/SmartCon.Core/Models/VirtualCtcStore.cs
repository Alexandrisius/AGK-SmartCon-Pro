using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Хранилище виртуальных CTC-назначений (ADR-002).
/// CTC хранятся в памяти; запись в семейство (LoadFamily) откладывается до Connect().
/// Ключ: (ElementId.Value, ConnectorIndex).
/// </summary>
public sealed class VirtualCtcStore
{
    private readonly Dictionary<(long ElemId, int ConnIdx), ConnectionTypeCode> _overrides = new();
    private readonly Dictionary<(long ElemId, int ConnIdx), ConnectorTypeDefinition> _pendingWrites = new();

    /// <summary>
    /// Установить виртуальный CTC для коннектора.
    /// <paramref name="typeDef"/> — полный ConnectorTypeDefinition для отложенной записи в семейство.
    /// Если typeDef == null, override создаётся без pending write (уже записано или MEPCurve).
    /// </summary>
    public void Set(ElementId elemId, int connIdx, ConnectionTypeCode ctc, ConnectorTypeDefinition? typeDef = null)
    {
        var key = (elemId.Value, connIdx);
        _overrides[key] = ctc;
        if (typeDef is not null)
            _pendingWrites[key] = typeDef;
    }

    /// <summary>Получить виртуальный CTC или null если не задан.</summary>
    public ConnectionTypeCode? Get(ElementId elemId, int connIdx)
    {
        return _overrides.TryGetValue((elemId.Value, connIdx), out var ctc) ? ctc : null;
    }

    /// <summary>
    /// Все виртуальные CTC для элемента, индексированные по ConnectorIndex.
    /// Для передачи в AlignFittingToStatic как ctcOverrides.
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
    /// Все отложенные записи CTC (для FlushVirtualCtcToFamilies в Connect()).
    /// </summary>
    public IReadOnlyList<(ElementId ElementId, int ConnectorIndex, ConnectorTypeDefinition TypeDef)> GetPendingWrites()
    {
        return _pendingWrites
            .Select(kvp => (new ElementId(kvp.Key.ElemId), kvp.Key.ConnIdx, kvp.Value))
            .ToList();
    }

    /// <summary>Есть ли хотя бы одна отложенная запись.</summary>
    public bool HasPendingWrites => _pendingWrites.Count > 0;

    /// <summary>Очистить все overrides и pending writes.</summary>
    public void Clear()
    {
        _overrides.Clear();
        _pendingWrites.Clear();
    }

    /// <summary>
    /// Перенести все overrides и pending writes со старого ElementId на новый.
    /// Используется при перевставке фитинга (delete + insert = новый ElementId).
    /// Старые записи удаляются.
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

    // ── Internal long-overloads для unit-тестов (без RevitAPI) ──────────────────

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
