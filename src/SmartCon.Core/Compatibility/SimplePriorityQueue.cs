#if !NET8_0
using System.Diagnostics.CodeAnalysis;

namespace SmartCon.Core.Compatibility;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("CodeQuality", "IDE0079:Remove unnecessary suppression")]
internal sealed class SimplePriorityQueue<TElement, TPriority> where TPriority : IComparable<TPriority>
{
    private readonly List<(TElement Element, TPriority Priority)> _heap = [];

    public int Count => _heap.Count;

    public void Enqueue(TElement element, TPriority priority)
    {
        _heap.Add((element, priority));
        SiftUp(_heap.Count - 1);
    }

    public TElement Dequeue()
    {
        if (_heap.Count == 0) throw new InvalidOperationException("Queue is empty");

        var result = _heap[0].Element;
        int lastIndex = _heap.Count - 1;
        _heap[0] = _heap[lastIndex];
        _heap.RemoveAt(lastIndex);
        SiftDown(0);
        return result;
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (_heap[index].Priority.CompareTo(_heap[parent].Priority) < 0)
            {
                (_heap[index], _heap[parent]) = (_heap[parent], _heap[index]);
                index = parent;
            }
            else break;
        }
    }

    private void SiftDown(int index)
    {
        int count = _heap.Count;
        while (true)
        {
            int left = 2 * index + 1;
            int right = 2 * index + 2;
            int smallest = index;

            if (left < count && _heap[left].Priority.CompareTo(_heap[smallest].Priority) < 0)
                smallest = left;
            if (right < count && _heap[right].Priority.CompareTo(_heap[smallest].Priority) < 0)
                smallest = right;

            if (smallest != index)
            {
                (_heap[index], _heap[smallest]) = (_heap[smallest], _heap[index]);
                index = smallest;
            }
            else break;
        }
    }
}
#endif
