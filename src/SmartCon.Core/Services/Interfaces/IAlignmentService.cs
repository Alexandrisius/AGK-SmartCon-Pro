using Autodesk.Revit.DB;
using SmartCon.Core.Math;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Aligns an element to a target connector using pre-computed or on-the-fly alignment results.
/// Implementation: SmartCon.Revit/Transform/RevitAlignmentService.cs.
/// </summary>
public interface IAlignmentService
{
    /// <summary>
    /// Align <paramref name="elementId"/> so its connector matches <paramref name="alignTarget"/>
    /// in position and orientation. Computes alignment on the fly.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="elementId">Element to align.</param>
    /// <param name="alignTarget">Target connector to match.</param>
    /// <param name="currentElement">Current state of the element's connector.</param>
    void ApplyAlignment(
        Document doc,
        ElementId elementId,
        ConnectorProxy alignTarget,
        ConnectorProxy currentElement);

    /// <summary>
    /// Apply a pre-computed <see cref="AlignmentResult"/> to an element.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="elementId">Element to align.</param>
    /// <param name="alignResult">Pre-computed alignment from <c>ConnectorAligner.ComputeAlignment</c>.</param>
    /// <param name="positionCorrectionConnIdx">
    /// Connector index used for position correction after rotation. -1 = skip correction.
    /// </param>
    void ApplyAlignment(
        Document doc,
        ElementId elementId,
        AlignmentResult alignResult,
        int positionCorrectionConnIdx = -1);
}
