using SmartCon.Core.Math;

namespace SmartCon.Core.Models;

/// <summary>
/// Adjustment of a straight pipe location curve: endpoint deltas in internal units.
/// Applied as <c>Line.CreateBound(start + StartDelta, end + EndDelta)</c>.
/// When the pipe absorbs displacement, StartDelta ≠ EndDelta (length changes);
/// when it cannot absorb, StartDelta == EndDelta (pure translation, remainder
/// propagates to the next chain level).
/// </summary>
public sealed record PipeAdjustOp
{
    /// <summary>ElementId value of the pipe.</summary>
    public required long ElementId { get; init; }

    /// <summary>Delta applied to location curve endpoint 0 (internal units).</summary>
    public required Vec3 StartDelta { get; init; }

    /// <summary>Delta applied to location curve endpoint 1 (internal units).</summary>
    public required Vec3 EndDelta { get; init; }

    /// <summary>
    /// Signed length change performed by this pipe (internal units).
    /// Positive = shortened, negative = lengthened, 0 = pure translation.
    /// </summary>
    public required double AbsorbedLengthFt { get; init; }
}
