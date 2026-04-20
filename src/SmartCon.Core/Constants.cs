namespace SmartCon.Core;

/// <summary>
/// Conversion factors between Revit Internal Units (decimal feet) and metric.
/// All SmartCon core algorithms work in Internal Units (I-02).
/// </summary>
public static class Units
{
    /// <summary>Multiply feet by this to get millimeters. 1 ft = 304.8 mm.</summary>
    public const double FeetToMm = 304.8;

    /// <summary>Multiply millimeters by this to get feet. 1 mm ≈ 0.003281 ft.</summary>
    public const double MmToFeet = 1.0 / 304.8;
}

/// <summary>
/// Standard tolerance values used across SmartCon for geometry comparisons.
/// All values in Internal Units (decimal feet) unless noted otherwise.
/// </summary>
public static class Tolerance
{
    /// <summary>Radius comparison tolerance (~0.003 mm). Used for connector radius matching.</summary>
    public const double RadiusFt = 1e-5;

    /// <summary>Position comparison tolerance (~0.03 mm). Used for connector origin matching.</summary>
    public const double PositionFt = 1e-4;

    /// <summary>Angle comparison tolerance in degrees. Used for BasisZ anti-parallel checks.</summary>
    public const double AngleDeg = 1.0;

    /// <summary>General-purpose double comparison (≈ 1e-9, Revit standard).</summary>
    public const double Default = 1e-9;

    /// <summary>Connector position match in family space (~0.3 mm). Used for EditFamily connector lookup.</summary>
    public const double ConnectorPositionMatch = 1e-3;

    /// <summary>Lookup table radius comparison tolerance in mm.</summary>
    public const double LookupRadiusMatchMm = 0.02;

    /// <summary>Relaxed position tolerance for validation (0.1 mm).</summary>
    public const double PositionRelaxedMm = 0.1;

    public const double RadiusComparison = 1e-6;

    public const double AxisParallelDot = 0.9999;

    public const double CollinearityThreshold = 0.9;
}

public static class Lookup
{
    public const double ConstraintMatchMm = 0.02;

    public const double ConstraintFallbackMm = 0.5;

    public const double SolverVerification = 1e-6;

    public const double BisectionFallbackTolerance = 0.01;
}

/// <summary>
/// Score thresholds for connector matching in family space (EditFamily).
/// Score=ExactPosition → exact origin match; Score≥DirectionThreshold → direction match.
/// </summary>
public static class ConnectorMatchScore
{
    /// <summary>Score assigned when connector origin matches exactly (distLocal &lt; Tolerance.ConnectorPositionMatch).</summary>
    public const double ExactPosition = 2.0;

    /// <summary>Minimum score for a valid direction-based connector match.</summary>
    public const double DirectionThreshold = 0.99;
}
