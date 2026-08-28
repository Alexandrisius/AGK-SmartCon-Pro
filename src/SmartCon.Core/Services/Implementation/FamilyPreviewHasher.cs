using System.Globalization;
using System.Text;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// VIEW3D preview hash (Issue #249, Phase 5): SHA-256 of the normalized
/// per-type GLB INPUTS — the GLB-filtered solid forms (FHV12-strengthened
/// metrics + resolved RGBA + visibility flags) and the nested instance
/// placements (symbol identity + transform). Hashes INPUTS, never the
/// tessellated bytes (tessellation vertex counts are unstable across
/// regenerations — Autodesk forum). ElementIds and family/type names are
/// NOT part of the input: a type RENAME reuses the same pooled GLB (the
/// name is the asset row's key, not content). The hash keys the shared
/// CAS preview pool (<c>files/_shared/models/{shard2}/{hash}.glb</c>) —
/// identical preview content is stored once, across versions AND across
/// families.
/// </summary>
public static class FamilyPreviewHasher
{
    /// <summary>
    /// Compute the VIEW3D hash of one type's preview inputs.
    /// <c>null</c> for a null snapshot (an empty snapshot — no forms and
    /// no nested instances — hashes deterministically like an empty
    /// GEOM section).
    /// </summary>
    public static string? ComputeForType(PreviewTypeSnapshot? preview)
    {
        if (preview is null)
        {
            return null;
        }
        return FamilyContentHasher.ComputeSha256Hex(BuildCanonicalString(preview));
    }

    /// <summary>
    /// The canonical input string: <c>VIEW3D|2|FORMS|…|NESTED|…|</c>.
    /// Form entries use the FHV12 GEOM field layout and formatting
    /// (invariant culture, 1e-4 ft coordinates, "0.######" metrics) plus
    /// the FHV18 per-face color histogram (#251); entries are sorted by
    /// their FULL content so the extraction order can never leak
    /// (validator H1). Format marker 2 = FHV18 (face-color histogram).
    /// </summary>
    internal static string BuildCanonicalString(PreviewTypeSnapshot preview)
    {
        var sb = new StringBuilder(256);
        sb.Append("VIEW3D|2|FORMS|");
        foreach (var entry in preview.Forms
            .Select(BuildFormEntry)
            .OrderBy(e => e, StringComparer.Ordinal))
        {
            sb.Append(entry);
        }
        sb.Append("NESTED|");
        foreach (var entry in preview.NestedInstances
            .Select(BuildNestedEntry)
            .OrderBy(e => e, StringComparer.Ordinal))
        {
            sb.Append(entry);
        }
        return sb.ToString();
    }

    private static string BuildFormEntry(FormMetrics f)
    {
        var sb = new StringBuilder(128);
        sb.Append(f.FormKind).Append('|');
        sb.Append(f.IsSolid ? 'S' : 'V').Append('|');
        sb.Append(f.Volume.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(f.FaceCount).Append('|');
        sb.Append(f.EdgeCount).Append('|');
        sb.Append(FamilyContentHasher.Escape(f.SubcategoryName ?? "NOSUBCAT")).Append('|');
        sb.Append(f.SurfaceArea.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        if (f.Bounds is not null)
        {
            sb.Append(FamilyContentHasher.FormatCoord(f.Bounds.MinX)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(f.Bounds.MinY)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(f.Bounds.MinZ)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(f.Bounds.MaxX)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(f.Bounds.MaxY)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(f.Bounds.MaxZ));
        }
        else
        {
            sb.Append('-');
        }
        sb.Append('|');
        if (f.Centroid is not null)
        {
            sb.Append(FamilyContentHasher.FormatCoord(f.Centroid.X)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(f.Centroid.Y)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(f.Centroid.Z));
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
            sb.Append(f.Visibility.IsVisibleParamValue?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(',');
            sb.Append(f.Visibility.IsShownInFine.HasValue ? (f.Visibility.IsShownInFine.Value ? "1" : "0") : "-");
        }
        else
        {
            sb.Append('-').Append(',').Append('-');
        }
        sb.Append('|');
        // FHV18 (#251): resolved per-face color histogram — a single-face
        // paint changes the GLB bytes (#108) and must re-key the pool.
        FamilyContentHasher.AppendFaceColors(sb, f.FaceColors);
        sb.Append('|');
        return sb.ToString();
    }

    private static string BuildNestedEntry(NestedInstanceSnapshot n)
    {
        var sb = new StringBuilder(96);
        sb.Append(FamilyContentHasher.Escape(n.FamilyName)).Append('|');
        sb.Append(FamilyContentHasher.Escape(n.SymbolName)).Append('|');
        sb.Append(FamilyContentHasher.FormatCoord(n.OriginX)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.OriginY)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.OriginZ)).Append('|');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisXx)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisXy)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisXz)).Append('|');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisYx)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisYy)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisYz)).Append('|');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisZx)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisZy)).Append(',');
        sb.Append(FamilyContentHasher.FormatCoord(n.BasisZz)).Append('|');
        sb.Append(n.IsVisibleParamValue?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|');
        // #250: the nested child's own content fingerprint (aggregate
        // symbol-geometry metrics + per-face colors) — a geometry or
        // material edit INSIDE the nested child re-keys the pool even when
        // the placement is untouched. '-' when not computed (a pre-#250 /
        // GEOM-context snapshot — deterministic, never crashes).
        AppendNestedContentMetrics(sb, n.ContentMetrics);
        sb.Append('|');
        return sb.ToString();
    }

    private static void AppendNestedContentMetrics(StringBuilder sb, FormMetrics? m)
    {
        if (m is null)
        {
            sb.Append('-');
            return;
        }
        sb.Append(m.Volume.ToString("0.######", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(m.SurfaceArea.ToString("0.######", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(m.FaceCount).Append(',');
        sb.Append(m.EdgeCount).Append(',');
        sb.Append(m.TotalEdgeLength.ToString("0.######", CultureInfo.InvariantCulture)).Append(',');
        if (m.Centroid is not null)
        {
            sb.Append(FamilyContentHasher.FormatCoord(m.Centroid.X)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(m.Centroid.Y)).Append(',');
            sb.Append(FamilyContentHasher.FormatCoord(m.Centroid.Z));
        }
        else
        {
            sb.Append('-');
        }
        sb.Append(';');
        FamilyContentHasher.AppendFaceColors(sb, m.FaceColors);
    }
}
