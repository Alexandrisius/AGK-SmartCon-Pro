using Autodesk.Revit.DB.ExtensibleStorage;

namespace SmartCon.Revit.Storage;

/// <summary>
/// ExtensibleStorage schema descriptor for SmartCon fitting mapping data (ADR-012).
/// Stores a single JSON string payload (<c>FittingMappingJsonSerializer</c>) inside a
/// <c>DataStorage</c> element of the active Revit project.
/// </summary>
/// <remarks>
/// The schema is registered lazily via <see cref="GetOrCreate"/>. Once registered in a
/// Revit process, <see cref="Schema.Lookup"/> returns the same instance for all documents.
/// Vendor/GUID values must not change between releases — changing them orphans existing payloads.
/// </remarks>
internal static class FittingMappingSchema
{
    /// <summary>
    /// Unique Schema identifier. Must not change after ADR-012 ships.
    /// </summary>
    public static readonly Guid SchemaGuid = new("4A5C3E1F-6B2D-4E8A-9C7F-12D3E4F5A6B7");

    public const string SchemaName = "SmartConFittingMappingSchema";

    /// <summary>
    /// Human-readable name assigned to the <c>DataStorage</c> element so users can
    /// identify SmartCon data when browsing project elements.
    /// </summary>
    public const string DataStorageName = "SmartCon.FittingMapping";

    public const string FieldSchemaVersion = "SchemaVersion";
    public const string FieldPayload = "Payload";

    /// <summary>
    /// Vendor id (case-insensitive, min 4 chars, letters/digits).
    /// Informational only — <see cref="AccessLevel.Public"/> write means the plugin's
    /// <c>&lt;VendorId&gt;</c> in <c>.addin</c> is not cross-checked by Revit
    /// (see <see cref="Build"/> for rationale).
    /// </summary>
    public const string VendorId = "AGKSMARTCON";

    /// <summary>
    /// Returns the existing registered schema or creates and registers a new one.
    /// Safe to call on any thread; <see cref="Schema.Lookup"/> is process-wide.
    /// </summary>
    public static Schema GetOrCreate()
    {
        return Schema.Lookup(SchemaGuid) ?? Build();
    }

    private static Schema Build()
    {
        using var builder = new SchemaBuilder(SchemaGuid);
        builder.SetVendorId(VendorId);
        builder.SetSchemaName(SchemaName);
        builder.SetDocumentation("Per-project fitting mapping for SmartCon plugin (ADR-012).");

        // ReadAccess=Public и WriteAccess=Public — сознательное решение:
        // VendorId в .addin равен "AGK" (3 символа), он невалиден для
        // Schema API (требует ≥4 символов). При WriteAccess=Vendor Revit
        // бросает InvalidOperationException "Writing of Entities of this
        // Schema is not allowed to the current add-in" при любой попытке
        // SetEntity. Защиту обеспечивает уникальный GUID схемы
        // (SchemaGuid) — другие плагины не пишут в наш DataStorage по
        // ошибке, т.к. у них другая Schema.
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);

        builder.AddSimpleField(FieldSchemaVersion, typeof(int));
        builder.AddSimpleField(FieldPayload, typeof(string));
        return builder.Finish();
    }
}
