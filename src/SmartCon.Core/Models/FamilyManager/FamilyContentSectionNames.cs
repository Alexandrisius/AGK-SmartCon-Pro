namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Canonical section names of the family content identity (Issue #249).
/// The full identity hash stays the SHA-256 of the concatenation of all
/// sections in canonical order; sections are an analytics layer on top
/// (per-section diff, change classification), never a replacement for the
/// single identity hash. Names match the markers embedded in the canonical
/// string (<c>PARAMS|</c>, <c>TYPES|</c>, …) so logs and diffs read the
/// same vocabulary as the format itself.
/// </summary>
public static class FamilyContentSectionNames
{
    /// <summary>Format prefix: FHV version, source kind, category identity.</summary>
    public const string Meta = "META";

    /// <summary>Parameter definitions + formulas (loadable only).</summary>
    public const string Params = "PARAMS";

    /// <summary>Family types and their parameter values.</summary>
    public const string Types = "TYPES";

    /// <summary>Parameter values of a typeless family (phantom default type).</summary>
    public const string Phantom = "PHANTOM";

    /// <summary>Form/dimension/reference-plane definition wiring (FHV12).</summary>
    public const string Def = "DEF";

    /// <summary>Per-form 3D geometry metrics.</summary>
    public const string Geom = "GEOM";

    /// <summary>2D content counters and lengths (curves, texts, planes).</summary>
    public const string Geom2d = "GEOM2D";

    /// <summary>Shared nested family names.</summary>
    public const string Nested = "NESTED";

    /// <summary>Non-shared nested family names.</summary>
    public const string NonShared = "NONSHARED";

    /// <summary>Composite hashes of direct shared-nested children.</summary>
    public const string NestedHash = "NESTEDHASH";

    /// <summary>Semantic facts (Part Type and similar category facts).</summary>
    public const string Facts = "FACTS";

    /// <summary>Behavior flags (shared, work-plane based, …).</summary>
    public const string Flags = "FLAGS";

    /// <summary>MEP connectors.</summary>
    public const string Conn = "CONN";

    /// <summary>Embedded lookup tables raw CSV (loadable only).</summary>
    public const string Lookup = "LOOKUP";

    /// <summary>Locale-invariant system family identity (system only).</summary>
    public const string FamKey = "FAMKEY";

    /// <summary>Compound structure layers (system only).</summary>
    public const string Struct = "STRUCT";

    /// <summary>Routing preferences (system only).</summary>
    public const string Routing = "ROUTING";

    /// <summary>Segment size tables (system only).</summary>
    public const string Segments = "SEGMENTS";

    /// <summary>Stairs subtype references (system only).</summary>
    public const string Subtypes = "SUBTYPES";

    /// <summary>Railing structure summary (system only).</summary>
    public const string Railing = "RAILING";

    /// <summary>Wire settings graph (system only).</summary>
    public const string Wire = "WIRE";

    /// <summary>
    /// Per-type entry of a system family holding the type name and its
    /// parameter values (the part of the canonical per-type body that
    /// precedes the <c>FAMKEY|</c> marker). Loadable families keep type
    /// values inside the <see cref="Types"/> section instead.
    /// </summary>
    public const string Values = "VALUES";
}
