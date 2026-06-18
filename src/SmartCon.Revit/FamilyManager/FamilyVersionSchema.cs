using Autodesk.Revit.DB.ExtensibleStorage;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// ExtensibleStorage schema for the per-family version marker (ADR-030, Phase 24).
/// Stored on the <c>Family</c> element inside the project document. Replaces
/// the <c>project_usage</c> SQLite table from Phase 23.
/// </summary>
/// <remarks>
/// <para>Schema lifetime: the schema is registered lazily via
/// <see cref="GetOrCreate"/>. Once registered in a Revit process,
/// <see cref="Schema.Lookup"/> returns the same instance for all documents.</para>
/// <para>The <c>SchemaGuid</c> and <c>VendorId</c> values must never change between
/// releases — changing them orphans every existing family version marker.</para>
/// </remarks>
internal static class FamilyVersionSchema
{
    /// <summary>
    /// Unique Schema identifier. Generated once via <c>[guid]::NewGuid()</c>
    /// and frozen forever. Stored as a string literal so the value is reviewable
    /// in source control.
    /// </summary>
    public static readonly Guid SchemaGuid = new("36F59D37-6AAC-4A05-9543-32F14671B1D2");

    public const string SchemaName = "SmartCon_FamilyVersion_v1";

    /// <summary>
    /// Vendor id (case-insensitive, min 4 chars, letters/digits).
    /// Informational only — <see cref="AccessLevel.Public"/> write means the plugin's
    /// <c>&lt;VendorId&gt;</c> in <c>.addin</c> is not cross-checked by Revit
    /// (see <see cref="Build"/> for rationale).
    /// </summary>
    public const string VendorId = "AGKSMARTCON";

    public const string FieldSchemaVersion = "SchemaVersion";
    public const string FieldCatalogItemId = "CatalogItemId";
    public const string FieldVersionLabel = "VersionLabel";
    public const string FieldLoadedAtUtc = "LoadedAtUtc";
    public const string FieldSourceRevitVersion = "SourceRevitVersion";

    /// <summary>
    /// Returns the existing registered schema or creates and registers a new one.
    /// Safe to call on any thread; <see cref="Schema.Lookup"/> is process-wide.
    /// </summary>
    public static Schema GetOrCreate()
    {
        var existing = Schema.Lookup(SchemaGuid);
        if (existing is not null) return existing;
        SmartConLogger.Info(
            $"FamilyVersionSchema.GetOrCreate: schema {SchemaName} (Guid={SchemaGuid}) not found, " +
            "creating new one.");
        try
        {
            var built = Build();
            SmartConLogger.Info(
                $"FamilyVersionSchema.GetOrCreate: schema {SchemaName} created successfully.");
            return built;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"FamilyVersionSchema.GetOrCreate: FAILED to build schema {SchemaName}: " +
                $"{ex.GetType().Name}: {ex.Message}. [Action: ES read/write will fail at runtime]");
            throw;
        }
    }

    private static Schema Build()
    {
        using var builder = new SchemaBuilder(SchemaGuid);
        builder.SetVendorId(VendorId);
        builder.SetSchemaName(SchemaName);
        builder.SetDocumentation(
            "Per-family version marker for SmartCon FamilyManager (Phase 24, ADR-030). " +
            "Stored on the Family element in the project document. Used for on-demand stale detection.");

        // ReadAccess=Public and WriteAccess=Public — intentional workaround:
        // VendorId in .addin equals "AGK" (3 chars), it is invalid for the
        // Schema API (requires >= 4 chars). With WriteAccess=Vendor, Revit
        // throws InvalidOperationException "Writing of Entities of this
        // Schema is not allowed to the current add-in" on any SetEntity call.
        // The unique GUID is the actual security boundary — other plugins
        // cannot accidentally write into our schema because they do not have it.
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);

        builder.AddSimpleField(FieldSchemaVersion, typeof(int));
        builder.AddSimpleField(FieldCatalogItemId, typeof(string));
        builder.AddSimpleField(FieldVersionLabel, typeof(string));
        builder.AddSimpleField(FieldLoadedAtUtc, typeof(string));
        builder.AddSimpleField(FieldSourceRevitVersion, typeof(int));
        return builder.Finish();
    }
}
