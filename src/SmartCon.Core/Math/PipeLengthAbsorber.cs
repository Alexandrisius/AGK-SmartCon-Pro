using SmartCon.Core.Models;

namespace SmartCon.Core.Math;

/// <summary>
/// Per-pipe displacement absorption (pure math on Vec3, ADR-009, ADR-052).
///
/// When a chain element being aligned is itself a straight pipe and its
/// alignment is a pure translation, the pipe changes its length instead of
/// moving as a rigid body: the near end (facing the parent connector) follows
/// the alignment offset, the far end keeps only the non-absorbed remainder.
/// Displacement propagation through the chain stops here when absorption is
/// full — downstream levels get a zero (or reduced) alignment offset.
///
/// Rules:
/// 1. Shortening never makes the pipe shorter than minPipeLength
///    (монтажный минимум, <see cref="PipeAbsorption.MinPipeLengthMm"/>).
///    Lengthening is unlimited.
/// 2. The remainder (partial absorption, perpendicular component) stays in the
///    far-end delta and is handled by the next chain level as usual.
/// </summary>
public static class PipeLengthAbsorber
{
    /// <summary>
    /// Compute location curve endpoint deltas for absorbing the alignment offset.
    /// </summary>
    /// <param name="elementId">ElementId value of the pipe (for the op record).</param>
    /// <param name="pipeStart">Location curve endpoint 0 (internal units).</param>
    /// <param name="pipeEnd">Location curve endpoint 1 (internal units).</param>
    /// <param name="entryPoint">Origin of the connector facing the parent element.</param>
    /// <param name="offset">Alignment translation the pipe must follow (internal units).</param>
    /// <param name="minPipeLength">Minimum allowed pipe length after shortening (internal units).</param>
    /// <returns>Endpoint deltas, or null when the geometry is degenerate.</returns>
    public static PipeAdjustOp? Compute(
        long elementId,
        Vec3 pipeStart,
        Vec3 pipeEnd,
        Vec3 entryPoint,
        Vec3 offset,
        double minPipeLength)
    {
        if (VectorUtils.IsZero(offset))
            return null;

        double length = VectorUtils.DistanceTo(pipeStart, pipeEnd);
        if (length < VectorUtils.Tolerance)
            return null;

        bool nearIsStart = VectorUtils.DistanceTo(entryPoint, pipeStart)
            <= VectorUtils.DistanceTo(entryPoint, pipeEnd);
        var near = nearIsStart ? pipeStart : pipeEnd;
        var far = nearIsStart ? pipeEnd : pipeStart;
        var axisNearToFar = VectorUtils.Normalize(far - near);

        // Axial component of the offset along near→far.
        double axial = VectorUtils.DotProduct(offset, axisNearToFar);

        // Shortening is capped by the minimum mountable length; lengthening is free.
        double absorbed = axial > 0
            ? System.Math.Min(axial, System.Math.Max(0.0, length - minPipeLength))
            : axial;

        var nearDelta = offset;
        var farDelta = offset - axisNearToFar * absorbed;

        return new PipeAdjustOp
        {
            ElementId = elementId,
            StartDelta = nearIsStart ? nearDelta : farDelta,
            EndDelta = nearIsStart ? farDelta : nearDelta,
            AbsorbedLengthFt = absorbed,
        };
    }

    /// <summary>
    /// Compute a new point path for a flexible pipe absorbing the alignment offset:
    /// only the endpoint facing the parent moves by the full offset (a flex pipe
    /// bends, so no axial math is needed), every intermediate point is preserved
    /// exactly as the user arranged it.
    /// </summary>
    /// <param name="points">Current flex pipe points including both endpoints.</param>
    /// <param name="entryPoint">Origin of the connector facing the parent element.</param>
    /// <param name="offset">Alignment translation the flex pipe must follow.</param>
    /// <param name="minPathLength">Minimum allowed polyline path length after the change.</param>
    /// <returns>New point list, or null when the path would become too short or input is degenerate.</returns>
    public static IReadOnlyList<Vec3>? ComputeFlexPath(
        IReadOnlyList<Vec3> points,
        Vec3 entryPoint,
        Vec3 offset,
        double minPathLength)
    {
        if (points.Count < 2 || VectorUtils.IsZero(offset))
            return null;

        int lastIndex = points.Count - 1;
        bool nearIsStart = VectorUtils.DistanceTo(entryPoint, points[0])
            <= VectorUtils.DistanceTo(entryPoint, points[lastIndex]);

        var result = new List<Vec3>(points);
        int nearIndex = nearIsStart ? 0 : lastIndex;
        result[nearIndex] = result[nearIndex] + offset;

        double pathLength = 0.0;
        for (int i = 1; i < result.Count; i++)
            pathLength += VectorUtils.DistanceTo(result[i - 1], result[i]);

        return pathLength < minPathLength ? null : result;
    }
}
