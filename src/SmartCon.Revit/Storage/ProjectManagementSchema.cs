using Autodesk.Revit.DB.ExtensibleStorage;

namespace SmartCon.Revit.Storage;

internal static class ProjectManagementSchema
{
    public static readonly Guid SchemaGuid = new("7B8E2F3A-1C4D-4F6B-A5E9-8D3C2B1F0E4A");

    public const string SchemaName = "SmartConProjectManagementSchema";
    public const string DataStorageName = "SmartCon.ProjectManagement";
    public const string FieldSchemaVersion = "SchemaVersion";
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
        builder.SetDocumentation("Per-project share settings for SmartCon ProjectManagement module (ADR-013).");

        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);

        builder.AddSimpleField(FieldSchemaVersion, typeof(int));
        builder.AddSimpleField(FieldPayload, typeof(string));
        return builder.Finish();
    }
}
