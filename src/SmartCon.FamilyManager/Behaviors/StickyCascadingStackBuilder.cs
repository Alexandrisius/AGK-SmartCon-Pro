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
///   <item>Начинаем с <c>occupiedTop = 0</c> — высота уже занятая sticky-заголовками.</item>
///   <item>Ищем самую "близкую к viewport" (max <c>top</c>) категорию,
///         у которой <c>top &lt; occupiedTop</c> и которая ещё не в стеке.</item>
///   <item>Добавляем в стек путь от корня до этой категории, пропуская уже добавленные.</item>
///   <item>Увеличиваем <c>occupiedTop</c> на высоту новых заголовков.</item>
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

        var result = new List<int>();
        var included = new HashSet<int>();
        var occupiedTop = 0.0;
        var maxIterations = tops.Count * 2;

        for (int iteration = 0; iteration < maxIterations && occupiedTop < viewportHeight; iteration++)
        {
            var candidate = FindNextCandidate(tops, viewportHeight, occupiedTop, included);
            if (candidate < 0) break;

            var addedHeight = AppendPath(candidate, parentIndices, heights, included, result);
            occupiedTop += addedHeight;
        }

        return result;
    }

    private static int FindNextCandidate(
        IReadOnlyList<double> tops,
        double viewportHeight,
        double occupiedTop,
        HashSet<int> included)
    {
        var candidate = -1;
        var candidateTop = double.NegativeInfinity;

        for (int i = 0; i < tops.Count; i++)
        {
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
