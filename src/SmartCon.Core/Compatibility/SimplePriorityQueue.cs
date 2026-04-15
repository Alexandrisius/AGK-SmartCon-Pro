#if !NET8_0
using System.Diagnostics.CodeAnalysis;

namespace SmartCon.Core.Compatibility;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("CodeQuality", "IDE0079:Remove unnecessary suppression")]
internal sealed class SimplePriorityQueue<TElement, TPriority> where TPriority : IComparable<TPriority>
{
    private readonly List<(TElement Element, TPriority Priority)> _items = [];

    public int Count => _items.Count;

    public void Enqueue(TElement element, TPriority priority)
    {
        _items.Add((element, priority));
    }

    public TElement Dequeue()
    {
        if (_items.Count == 0) throw new InvalidOperationException("Queue is empty");

        int minIdx = 0;
        for (int i = 1; i < _items.Count; i++)
        {
            if (_items[i].Priority.CompareTo(_items[minIdx].Priority) < 0)
                minIdx = i;
        }

        var result = _items[minIdx].Element;
        _items.RemoveAt(minIdx);
        return result;
    }
}
#endif
