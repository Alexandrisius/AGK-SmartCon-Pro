namespace SmartCon.Core.Math;

/// <summary>
/// Result of ConnectorAligner alignment computation.
/// Contains a set of operations to apply in Revit via ITransformService.
/// All coordinates in Internal Units (decimal feet, I-02).
/// </summary>
public sealed class AlignmentResult
{
    /// <summary>
    /// Step 1: initial offset vector (static.Origin - dynamic.Origin).
    /// </summary>
    public required Vec3 InitialOffset { get; init; }

    /// <summary>
    /// Step 2: BasisZ rotation for anti-parallelism.
    /// null if BasisZ is already anti-parallel.
    /// </summary>
    public RotationStep? BasisZRotation { get; init; }

    /// <summary>
    /// Step 3: BasisX snap to the nearest "nice" angle.
    /// null if deltaAngle is approximately 0.
    /// </summary>
    public RotationStep? BasisXSnap { get; init; }

    /// <summary>
    /// Point relative to which rotations are performed (static.Origin).
    /// </summary>
    public required Vec3 RotationCenter { get; init; }
}

/// <summary>
/// A single rotation operation: axis + angle.
/// </summary>
public sealed record RotationStep(Vec3 Axis, double AngleRadians);
