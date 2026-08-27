namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Classification of a content change between two versions of a family
/// (Issue #249, Phase 4): how significant the drift is. Drives the batch
/// dialog's hint («косметические изменения — рассмотрите „Перезаписать
/// текущую"») and, later, the cloud moderation lane (trivial →
/// auto-publish, minor → quick review, major → full moderation).
/// </summary>
public enum ContentChangeClass
{
    /// <summary>No section differs — the versions are content-identical.</summary>
    None = 0,

    /// <summary>Only cosmetic sections changed (GEOM2D, FACTS, …).</summary>
    Trivial = 1,

    /// <summary>Type values, parameter schema, nesting, lookup tables,
    /// behavior flags changed — reviewable at a glance.</summary>
    Minor = 2,

    /// <summary>Geometry, definition wiring or connectors changed —
    /// the family's physical shape or behavior is different.</summary>
    Major = 3,
}
