using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Migrates the FamilyManager <c>registry.json</c> forward across schema
/// versions. Each version is a small, additive upgrade (default-fill of new
/// fields plus an atomic rewrite). The migrator runs once at add-in startup
/// right after the database manager has been initialized, before any UI
/// consumes the registry (see #119, decision A12).
/// </summary>
public interface IRegistryMigrator
{
    /// <summary>
    /// Ensure the on-disk registry file matches the latest schema supported
    /// by this build. Idempotent: a no-op when the file is already at the
    /// latest version. Safe to call on every startup.
    /// </summary>
    Task MigrateAsync(CancellationToken ct = default);

    /// <summary>Latest registry schema version this build knows how to read and write.</summary>
    int LatestSchemaVersion { get; }
}