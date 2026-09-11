namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Role of a cloud-linked database (cloud catalog, ADR-075 §7). Stored as a
/// separate <see cref="DatabaseConnection.CloudLink"/> marker — orthogonal to
/// <see cref="BaseType"/> (a published base can be General or Project-bound).
/// </summary>
public enum CloudLinkRole
{
    /// <summary>The database is published to a server catalog; local edits are pushed by the explicit publish command.</summary>
    Published = 0,

    /// <summary>The database is a local copy of a server catalog; read-only outside the sync session (pull-only).</summary>
    Subscribed = 1,
}

/// <summary>
/// Cloud catalog link of a database connection (master plan §7.3.1): the
/// registry (<c>registry.json</c>) copy is the runtime source; a duplicate is
/// stored in <c>catalog.db.database_meta.remote_source_json</c> for self-heal
/// when an older plugin rewrites the registry without the unknown field.
/// </summary>
/// <param name="Role">Whether this database publishes to or subscribes to the server catalog.</param>
/// <param name="Endpoint">Server base URL (<c>http(s)://host:port</c>). Credential Manager targets include it — accounts of different servers never collide.</param>
/// <param name="CatalogId">Server-side catalog identity (GUID string) — stable across renames.</param>
/// <param name="Slug">URL-friendly catalog identifier used in API paths and the local copy folder name.</param>
/// <param name="LastSyncedPublishSeq">Publish point seq of the last successful publish/pull; null = never synced.</param>
public sealed record CloudLink(
    CloudLinkRole Role,
    string Endpoint,
    string CatalogId,
    string Slug,
    long? LastSyncedPublishSeq = null);
