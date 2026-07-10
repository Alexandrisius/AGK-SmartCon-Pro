namespace SmartCon.UI.DragDrop;

/// <summary>
/// Pure helper for calculating pressure-based auto-scroll deltas during a drag operation.
/// No WPF dependencies — easy to unit test.
/// </summary>
public static class DragDropAutoScrollCalculator
{
    /// <summary>
    /// Returns true when the cursor is close enough to the top or bottom edge
    /// of the viewport to trigger vertical auto-scroll.
    /// </summary>
    /// <param name="cursorY">Cursor Y coordinate relative to the scrollable viewport.</param>
    /// <param name="viewportHeight">Height of the viewport.</param>
    /// <param name="topInset">Height of a non-scrollable strip at the top (e.g. sticky headers).</param>
    /// <param name="edgeTolerance">Thickness of the active edge zone in pixels.</param>
    public static bool IsInVerticalScrollZone(double cursorY, double viewportHeight, double topInset, double edgeTolerance)
    {
        if (viewportHeight <= 0 || edgeTolerance <= 0)
            return false;

        if (cursorY <= topInset + edgeTolerance)
            return true;

        if (cursorY >= viewportHeight - edgeTolerance)
            return true;

        return false;
    }

    /// <summary>
    /// Calculates the vertical delta for the next scroll step.
    /// Negative values scroll up, positive values scroll down, zero means no scrolling.
    /// </summary>
    /// <param name="cursorY">Cursor Y coordinate relative to the scrollable viewport.</param>
    /// <param name="viewportHeight">Height of the viewport.</param>
    /// <param name="topInset">Height of a non-scrollable strip at the top.</param>
    /// <param name="edgeTolerance">Thickness of the active edge zone in pixels.</param>
    /// <param name="maxSpeed">Maximum scrolling speed in pixels per second.</param>
    /// <param name="elapsedSeconds">Elapsed time since the last scroll step.</param>
    public static double ComputeVerticalDelta(
        double cursorY,
        double viewportHeight,
        double topInset,
        double edgeTolerance,
        double maxSpeed,
        double elapsedSeconds)
    {
        if (viewportHeight <= 0 || edgeTolerance <= 0 || elapsedSeconds <= 0 || maxSpeed <= 0)
            return 0;

        // Top zone: the closer to the edge, the faster we scroll up.
        if (cursorY <= topInset + edgeTolerance)
        {
            var distance = cursorY - topInset;
            if (distance < 0)
                distance = 0;

            var factor = 1.0 - distance / edgeTolerance;
            if (factor < 0)
                factor = 0;
            if (factor > 1)
                factor = 1;

            return -maxSpeed * factor * elapsedSeconds;
        }

        // Bottom zone: the closer to the edge, the faster we scroll down.
        if (cursorY >= viewportHeight - edgeTolerance)
        {
            var distance = viewportHeight - cursorY;
            if (distance < 0)
                distance = 0;

            var factor = 1.0 - distance / edgeTolerance;
            if (factor < 0)
                factor = 0;
            if (factor > 1)
                factor = 1;

            return maxSpeed * factor * elapsedSeconds;
        }

        return 0;
    }
}
