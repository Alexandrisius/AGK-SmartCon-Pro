namespace SmartCon.FamilyManager.Behaviors;

/// <summary>
/// Чистая логика построения каскада прилипающих заголовков с учётом занятой
/// ими высоты. Каждый следующий заголовок "прилипает" к низу уже показанных
/// sticky-заголовков, а не к верху viewport. Это решает проблему, когда
/// категории скрываются за уже закреплёнными строками, но ещё не достигли
/// верхней границы viewport.
/// </summary>
    /// <remarks>
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Начинаем с <c>occupiedTop = 0</c> и <c>lastPinnedIndex = -1</c>.</item>
    ///   <item>Ищем среди прямых детей последней закреплённой категории
    ///         (или корней, если <c>lastPinnedIndex = -1</c>) самую "близкую к viewport"
    ///         (max <c>top</c>) категорию, у которой <c>top &lt; occupiedTop</c>.</item>
    ///   <item>Добавляем найденную категорию в стек; её предки уже в стеке, поэтому
    ///         добавляется только сама категория.</item>
    ///   <item>Увеличиваем <c>occupiedTop</c> на высоту добавленного заголовка
    ///         и устанавливаем <c>lastPinnedIndex</c> = найденная категория.</item>
    ///   <item>Повторяем, пока находятся категории, ушедшие под sticky-bar.</item>
    /// </list>
    /// </remarks>
public static class StickyCascadingStackBuilder
{
    /// <summary>
    /// Построить каскад sticky-заголовков.
    /// </summary>
    /// <param name="tops">Y-позиция верха header для каждой Category (в координатах ScrollContentPresenter). <c>PositiveInfinity</c> = неизвестно.</param>
    /// <param name="heights">Высота header для каждой Category.</param>
    /// <param name="parentIndices">Для каждого индекса — индекс родителя или -1 для корня.</param>
    /// <param name="viewportHeight">Высота видимой области viewport.</param>
    /// <returns>Список индексов в порядке от корня к самой глубокой показанной Category.</returns>
    public static IReadOnlyList<int> BuildCascadingStack(
        IReadOnlyList<double> tops,
        IReadOnlyList<double> heights,
        IReadOnlyList<int> parentIndices,
        double viewportHeight)
    {
        if (tops.Count != heights.Count || tops.Count != parentIndices.Count)
            throw new ArgumentException("tops, heights и parentIndices должны быть одинаковой длины");

        // Без логирования: чистая функция, вызывается на каждый тик скролла.
        // Результат логирует вызывающий RecalculateSticky при смене стека.
        var result = new List<int>();
        var included = new HashSet<int>();
        var occupiedTop = 0.0;
        var lastPinnedIndex = -1;
        var maxIterations = tops.Count * 2;

        for (int iteration = 0; iteration < maxIterations && occupiedTop < viewportHeight; iteration++)
        {
            var candidate = FindNextCandidate(tops, parentIndices, lastPinnedIndex, viewportHeight, occupiedTop, included);
            if (candidate < 0) break;

            var addedHeight = AppendPath(candidate, parentIndices, heights, included, result);
            occupiedTop += addedHeight;
            lastPinnedIndex = candidate;
        }

        return result;
    }

    private static int FindNextCandidate(
        IReadOnlyList<double> tops,
        IReadOnlyList<int> parentIndices,
        int lastPinnedIndex,
        double viewportHeight,
        double occupiedTop,
        HashSet<int> included)
    {
        var candidate = -1;
        var candidateTop = double.NegativeInfinity;

        for (int i = 0; i < tops.Count; i++)
        {
            if (parentIndices[i] != lastPinnedIndex) continue;
            if (included.Contains(i)) continue;

            var top = tops[i];
            if (double.IsPositiveInfinity(top)) continue;
            if (top >= viewportHeight) continue;
            if (top < occupiedTop && top > candidateTop)
            {
                candidate = i;
                candidateTop = top;
            }
        }

        return candidate;
    }

    private static double AppendPath(
        int leafIndex,
        IReadOnlyList<int> parentIndices,
        IReadOnlyList<double> heights,
        HashSet<int> included,
        List<int> result)
    {
        var path = new List<int>();
        var current = leafIndex;
        while (current >= 0)
        {
            if (!included.Contains(current))
                path.Add(current);
            current = parentIndices[current];
        }

        path.Reverse();

        var addedHeight = 0.0;
        foreach (var idx in path)
        {
            result.Add(idx);
            included.Add(idx);
            addedHeight += heights[idx] > 0 ? heights[idx] : 0;
        }

        return addedHeight;
    }
}
