using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    /// <summary>
    /// Connector elements of the family (ADR-056, Issue #159): domain,
    /// profile, sizes, system classification, origin and intra-family
    /// linkage. Returned pre-sorted (Domain, Shape, SystemClassification,
    /// Origin) with <see cref="ConnectorSnapshot.LinkedIndex"/> computed
    /// against that order — the hasher re-sorts with the identical key,
    /// which is a no-op for this list. Every property read is isolated:
    /// a connector whose size is not applicable to its profile yields
    /// <c>null</c>, never an exception.
    /// </summary>
    private static List<ConnectorSnapshot> ExtractConnectors(Document familyDoc)
    {
        try
        {
            var elements = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(ConnectorElement))
                .Cast<ConnectorElement>()
                .ToList();

            if (elements.Count == 0)
                return new List<ConnectorSnapshot>(0);

            var sorted = elements
                .OrderBy(el => SafeDomain(el))
                .ThenBy(el => SafeShape(el))
                .ThenBy(el => SafeSystemClassification(el))
                .ThenBy(el => SafeOrigin(el)?.X ?? 0)
                .ThenBy(el => SafeOrigin(el)?.Y ?? 0)
                .ThenBy(el => SafeOrigin(el)?.Z ?? 0)
                .ToList();

            var indexByElementId = new Dictionary<long, int>(sorted.Count);
            for (var i = 0; i < sorted.Count; i++)
            {
                indexByElementId[GetElementIdValue(sorted[i].Id)] = i;
            }

            var result = new List<ConnectorSnapshot>(sorted.Count);
            foreach (var el in sorted)
            {
                var origin = SafeOrigin(el);
                var linkedIndex = -1;
                try
                {
                    var linked = el.GetLinkedConnectorElement();
                    if (linked is not null &&
                        indexByElementId.TryGetValue(GetElementIdValue(linked.Id), out var found))
                    {
                        linkedIndex = found;
                    }
                }
                catch
                {
                    // no linked connector
                }

                result.Add(new ConnectorSnapshot(
                    Domain: SafeDomain(el),
                    Shape: SafeShape(el),
                    SystemClassification: SafeSystemClassification(el),
                    IsPrimary: SafeIsPrimary(el),
                    Width: SafeDimension(el, nameof(ConnectorElement.Width)),
                    Height: SafeDimension(el, nameof(ConnectorElement.Height)),
                    Radius: SafeDimension(el, nameof(ConnectorElement.Radius)),
                    OriginX: origin?.X ?? 0,
                    OriginY: origin?.Y ?? 0,
                    OriginZ: origin?.Z ?? 0,
                    LinkedIndex: linkedIndex));
            }

            return result;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Connector scan failed: {ex.Message}");
            return new List<ConnectorSnapshot>(0);
        }
    }

    private static int SafeDomain(ConnectorElement el)
    {
        try { return (int)el.Domain; }
        catch { return 0; }
    }

    private static int SafeShape(ConnectorElement el)
    {
        try { return (int)el.Shape; }
        catch { return 0; }
    }

    private static int SafeSystemClassification(ConnectorElement el)
    {
        try { return (int)el.SystemClassification; }
        catch { return 0; }
    }

    private static bool SafeIsPrimary(ConnectorElement el)
    {
        try { return el.IsPrimary; }
        catch { return false; }
    }

    private static XYZ? SafeOrigin(ConnectorElement el)
    {
        try { return el.Origin; }
        catch { return null; }
    }

    private static double? SafeDimension(ConnectorElement el, string propertyName)
    {
        try
        {
            return propertyName switch
            {
                nameof(ConnectorElement.Width) => el.Width,
                nameof(ConnectorElement.Height) => el.Height,
                nameof(ConnectorElement.Radius) => el.Radius,
                _ => null,
            };
        }
        catch
        {
            // dimension not applicable to this profile (e.g. Radius on rectangular)
            return null;
        }
    }

    private static long GetElementIdValue(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }
}
