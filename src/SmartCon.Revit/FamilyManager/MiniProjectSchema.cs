using Autodesk.Revit.DB.ExtensibleStorage;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// ExtensibleStorage schema descriptor for the SmartCon mini-project marker
/// (Issue #188). A single <c>DataStorage</c> element named
/// <see cref="DataStorageName"/> in a staged mini-project carries the entity
/// that flags the file as a SmartCon system-family reference.
/// </summary>
/// <remarks>
/// DataStorage (not ProjectInfo) per Jeremy Tammik's recommendation — a
/// dedicated element avoids element-ownership contention on the ProjectInfo
/// singleton in workshared files and collisions with other add-ins. VendorId
/// uses the ≥4-char workaround ("AGKSMARTCON"), see known-workarounds.md.
/// GUID must never change after release — changing it orphans existing markers.
/// </remarks>
internal static class MiniProjectSchema
{
    public static readonly Guid SchemaGuid = new("8C3F1A7E-2B4D-4E6A-9F5C-7D1E3B2A4C68");

    public const string SchemaName = "SmartCon_MiniProject_v1";
    public const string DataStorageName = "SmartCon.MiniProject";
    public const string VendorId = "AGKSMARTCON";

    public const int CurrentSchemaVersion = 1;

    public const string FieldSchemaVersion = "SchemaVersion";
    public const string FieldCatalogItemId = "CatalogItemId";
    public const string FieldMarkedAtUtc = "MarkedAtUtc";

    public static Schema GetOrCreate()
    {
        return Schema.Lookup(SchemaGuid) ?? Build();
    }

    private static Schema Build()
    {
        using var builder = new SchemaBuilder(SchemaGuid);
        builder.SetVendorId(VendorId);
        builder.SetSchemaName(SchemaName);
        builder.SetDocumentation(
            "Marks this document as a SmartCon reference mini-project " +
            "(staged system family types, Issue #188). Such documents are " +
            "closed without saving after reimport and excluded from " +
            "active-database auto-switching.");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);

        builder.AddSimpleField(FieldSchemaVersion, typeof(int));
        builder.AddSimpleField(FieldCatalogItemId, typeof(string));
        builder.AddSimpleField(FieldMarkedAtUtc, typeof(string));
        return builder.Finish();
    }
}
