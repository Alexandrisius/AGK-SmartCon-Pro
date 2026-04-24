using Autodesk.Revit.DB.ExtensibleStorage;

namespace SmartCon.Revit.Storage;

internal static class ExportNameOverrideSchema
{
    public static readonly Guid SchemaGuid = new("9A3F7E2B-5D8C-4A1F-B6E0-7C9D2A3F8E1B");

    public const string SchemaName = "SmartConExportNameOverrideSchema";
    public const string DataStorageName = "SmartCon.ExportNameOverride";
    public const string FieldPayload = "Payload";
    public const string VendorId = "AGKSMARTCON";

    public static Schema GetOrCreate()
    {
        return Schema.Lookup(SchemaGuid) ?? Build();
    }

    private static Schema Build()
    {
        using var builder = new SchemaBuilder(SchemaGuid);
        builder.SetVendorId(VendorId);
        builder.SetSchemaName(SchemaName);
        builder.SetDocumentation("Per-project export name override for SmartCon ProjectManagement module.");

        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);

        builder.AddSimpleField(FieldPayload, typeof(string));
        return builder.Finish();
    }
}
