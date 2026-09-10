namespace SmartCon.Core.Services;

/// <summary>Причина остановки обхода цепочки уровней.</summary>
public enum ChainTraversalStopReason
{
    /// <summary>Достигнут целевой уровень или лимит.</summary>
    Completed,

    /// <summary>Шаг вернул ошибку (null).</summary>
    StepFailed,

    /// <summary>Шаг завершился успешно, но глубина не продвинулась — защита от бесконечного цикла (см. issue #137).</summary>
    NoProgress,
}

public readonly record struct ChainTraversalResult(
    int FinalDepth,
    int Processed,
    ChainTraversalStopReason StopReason);

/// <summary>
/// Драйвер обхода цепочки уровней с непробиваемой защитой от бесконечного цикла.
/// Шаг обязан либо продвинуть глубину, либо вернуть null (ошибка) — иначе обход останавливается.
/// </summary>
public static class ChainTraversalRunner
{
    /// <param name="startDepth">Текущая глубина цепочки.</param>
    /// <param name="targetLevel">Целевой уровень обхода.</param>
    /// <param name="maxLevel">Жёсткий лимит глубины.</param>
    /// <param name="step">Шаг обхода: возвращает новую глубину или null при ошибке.</param>
    public static ChainTraversalResult Run(int startDepth, int targetLevel, int maxLevel, Func<int?> step)
    {
#if NETFRAMEWORK
        if (step is null) throw new ArgumentNullException(nameof(step));
#else
        ArgumentNullException.ThrowIfNull(step);
#endif

        var depth = startDepth;
        var processed = 0;

        while (depth < targetLevel && depth < maxLevel)
        {
            var next = step();
            if (next is null)
                return new ChainTraversalResult(depth, processed, ChainTraversalStopReason.StepFailed);

            if (next.Value <= depth)
                return new ChainTraversalResult(depth, processed, ChainTraversalStopReason.NoProgress);

            depth = next.Value;
            processed++;
        }

        return new ChainTraversalResult(depth, processed, ChainTraversalStopReason.Completed);
    }
}
