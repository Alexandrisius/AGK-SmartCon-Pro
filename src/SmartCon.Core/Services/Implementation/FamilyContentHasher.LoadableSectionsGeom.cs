using System.Globalization;
using System.Text;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

public sealed partial class FamilyContentHasher
{
    private static string BuildGeomSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(256);
        sb.Append("GEOM|");
        sb.Append(snapshot.Geometry.TotalFormCount).Append('|');
        // FHV17 (#249, manual-test round 5): forms AND nested instances are
        // sorted by their full EMITTED canonical entry (Ordinal). FHV12-16
        // sorted by raw doubles — but the emission quantizes (FormatCoord /
        // 0.######), so the sort key carried sub-quantization regen noise:
        // two extractions of an unchanged family (e.g. after a text-only
        // parameter edit re-rolled the last FP bits) produced the same
        // emitted entries in a DIFFERENT order, flipping the GEOM hash and
        // marking every loaded type stale. Sorting by the emitted string
        // makes the canonical order identical to the canonical content by
        // construction.
        var formEntries = snapshot.Geometry.Forms
            .Select(BuildGeomFormEntry)
            .OrderBy(e => e, StringComparer.Ordinal);
        foreach (var entry in formEntries)
        {
            sb.Append(entry);
        }

        // FHV12 (#249, Phase 3): nested FamilyInstance placements — symbol
        // identity + quantized transform + visibility. Pre-FHV12 only the
        // nested family NAMES were hashed (NESTED section): moving or
        // rotating a nested part passed the hash silently.
        sb.Append("NESTEDINST|");
        var instanceEntries = (snapshot.Geometry.NestedInstances ?? (IReadOnlyList<NestedInstanceSnapshot>)[])
            .Select(BuildNestedInstanceEntry)
            .OrderBy(e => e, StringComparer.Ordinal);
        foreach (var entry in instanceEntries)
        {
            sb.Append(entry);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Full canonical entry of one GEOM form (ends with the '|' separator).
    /// The entry doubles as its own sort key (FHV17) — see
    /// <see cref="BuildGeomSection"/>.
    /// </summary>
    private static string BuildGeomFormEntry(FormMetrics f)
    {
        var sb = new StringBuilder(96);
        sb.Append(f.FormKind).Append('|');
        sb.Append(f.IsSolid ? 'S' : 'V').Append('|');
        sb.Append(f.Volume.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(f.FaceCount).Append('|');
        sb.Append(f.EdgeCount).Append('|');
        sb.Append(Escape(f.SubcategoryName ?? NullSubcatMarker)).Append('|');
        sb.Append(f.SurfaceArea.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        if (f.Bounds is not null)
        {
            sb.Append(FormatCoord(f.Bounds.MinX)).Append(',');
            sb.Append(FormatCoord(f.Bounds.MinY)).Append(',');
            sb.Append(FormatCoord(f.Bounds.MinZ)).Append(',');
            sb.Append(FormatCoord(f.Bounds.MaxX)).Append(',');
            sb.Append(FormatCoord(f.Bounds.MaxY)).Append(',');
            sb.Append(FormatCoord(f.Bounds.MaxZ));
        }
        else
        {
            sb.Append('-');
        }
        sb.Append('|');

        // FHV12 (#249, Phase 3): strengthened per-form metrics.
        if (f.Centroid is not null)
        {
            sb.Append(FormatCoord(f.Centroid.X)).Append(',');
            sb.Append(FormatCoord(f.Centroid.Y)).Append(',');
            sb.Append(FormatCoord(f.Centroid.Z));
        }
        else
        {
            sb.Append('-');
        }
        sb.Append('|');
        if (f.FaceTypes is { Count: > 0 } faceTypes)
        {
            var first = true;
            foreach (var ft in faceTypes.OrderBy(t => t.FaceKind, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                sb.Append(ft.FaceKind).Append(':').Append(ft.Count);
                first = false;
            }
        }
        else
        {
            sb.Append('-');
        }
        sb.Append('|');
        sb.Append(f.TotalEdgeLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        if (f.MaterialColor is not null)
        {
            sb.Append(f.MaterialColor.R).Append(',');
            sb.Append(f.MaterialColor.G).Append(',');
            sb.Append(f.MaterialColor.B).Append(',');
            sb.Append(f.MaterialColor.A);
        }
        else
        {
            sb.Append('-');
        }
        sb.Append('|');
        if (f.Visibility is not null)
        {
            sb.Append(f.Visibility.IsVisibleParamValue?.ToString(CultureInfo.InvariantCulture) ?? AbsentMarker).Append(',');
            sb.Append(FormatFlag(f.Visibility.IsShownInFine));
        }
        else
        {
            sb.Append('-').Append(',').Append('-');
        }
        sb.Append('|');
        // FHV18 (#251): resolved per-face color histogram — painting ONE
        // face changes the GLB bytes (#108); pre-FHV18 both CAS tiers and
        // this section carried only the single form-level color and reused
        // the stale preview silently.
        AppendFaceColors(sb, f.FaceColors);
        sb.Append('|');
        return sb.ToString();
    }

    /// <summary>
    /// Full canonical entry of one nested FamilyInstance placement (ends
    /// with the '|' separator); doubles as its own sort key (FHV17).
    /// </summary>
    private static string BuildNestedInstanceEntry(NestedInstanceSnapshot n)
    {
        var sb = new StringBuilder(80);
        sb.Append(Escape(n.FamilyName)).Append('|');
        sb.Append(Escape(n.SymbolName)).Append('|');
        sb.Append(FormatCoord(n.OriginX)).Append(',');
        sb.Append(FormatCoord(n.OriginY)).Append(',');
        sb.Append(FormatCoord(n.OriginZ)).Append('|');
        sb.Append(FormatCoord(n.BasisXx)).Append(',');
        sb.Append(FormatCoord(n.BasisXy)).Append(',');
        sb.Append(FormatCoord(n.BasisXz)).Append('|');
        sb.Append(FormatCoord(n.BasisYx)).Append(',');
        sb.Append(FormatCoord(n.BasisYy)).Append(',');
        sb.Append(FormatCoord(n.BasisYz)).Append('|');
        sb.Append(FormatCoord(n.BasisZx)).Append(',');
        sb.Append(FormatCoord(n.BasisZy)).Append(',');
        sb.Append(FormatCoord(n.BasisZz)).Append('|');
        sb.Append(n.IsVisibleParamValue?.ToString(CultureInfo.InvariantCulture) ?? AbsentMarker).Append('|');
        return sb.ToString();
    }

    /// <summary>
    /// Full canonical entry of one DEF bound form (ends with the '|'
    /// separator); doubles as its own sort key (FHV17).
    /// </summary>
    private static string BuildDefBoundFormEntry(FormDefinitionSnapshot f)
    {
        var sb = new StringBuilder(64);
        sb.Append(f.FormKind).Append('|');
        sb.Append(f.IsSolid ? 'S' : 'V').Append('|');
        sb.Append(Escape(f.SubcategoryName ?? NullSubcatMarker)).Append('|');
        sb.Append(Escape(f.VisibilityParameterName ?? AbsentMarker)).Append('|');
        sb.Append(Escape(f.MaterialParameterName ?? AbsentMarker)).Append('|');
        sb.Append(Escape(f.ExtrusionStartParameterName ?? AbsentMarker)).Append('|');
        sb.Append(Escape(f.ExtrusionEndParameterName ?? AbsentMarker)).Append('|');
        sb.Append(f.ExtrusionStartOffset.HasValue
            ? f.ExtrusionStartOffset.Value.ToString("0.######", CultureInfo.InvariantCulture)
            : AbsentMarker).Append('|');
        sb.Append(f.ExtrusionEndOffset.HasValue
            ? f.ExtrusionEndOffset.Value.ToString("0.######", CultureInfo.InvariantCulture)
            : AbsentMarker).Append('|');
        return sb.ToString();
    }

    private static string BuildGeom2dSection(FamilySnapshot snapshot)
    {
        // FHV14 (#249 follow-up): pure 2D GRAPHICS only — reference planes
        // and dimensions moved out (DEF owns the wiring: PLANES list and
        // labeled DIMS); their counts here only duplicated that signal.
        var sb = new StringBuilder(96);
        sb.Append("GEOM2D|");
        sb.Append(snapshot.Geometry.SymbolicCurveCount).Append('|');
        sb.Append(snapshot.Geometry.DetailCurveCount).Append('|');
        sb.Append(snapshot.Geometry.ModelCurveCount).Append('|');
        sb.Append(snapshot.Geometry.TextNoteCount).Append('|');
        sb.Append(snapshot.Geometry.TotalSymbolicCurveLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(snapshot.Geometry.TotalDetailCurveLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(snapshot.Geometry.TotalModelCurveLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        return sb.ToString();
    }
}
