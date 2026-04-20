namespace SmartCon.Core.Math;

/// <summary>
/// Result of connector alignment computation (<see cref="ConnectorAligner.ComputeAlignment"/>).
/// Describes the translation and rotation steps needed to align two connectors.
/// </summary>
public sealed record AlignmentResult
{
    /// <summary>Initial translation offset to bring connectors into the same plane.</summary>
    public required Vec3 InitialOffset { get; init; }

    /// <summary>Rotation around BasisZ to align connector directions. Null if not needed.</summary>
    public RotationStep? BasisZRotation { get; init; }

    /// <summary>Snap rotation around the perpendicular axis for anti-parallel alignment. Null if not needed.</summary>
    public RotationStep? BasisXSnap { get; init; }

    /// <summary>Center point for rotation operations.</summary>
    public required Vec3 RotationCenter { get; init; }
}

/// <summary>A single rotation step defined by axis and angle.</summary>
/// <param name="Axis">Rotation axis direction.</param>
/// <param name="AngleRadians">Rotation angle in radians.</param>
public sealed record RotationStep(Vec3 Axis, double AngleRadians);
