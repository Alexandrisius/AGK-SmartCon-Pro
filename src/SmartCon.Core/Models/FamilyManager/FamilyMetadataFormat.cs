namespace SmartCon.Core.Models.FamilyManager;

public static class FamilyMetadataFormat
{
    public const string Id = "smartcon.familymanager.metadata-package";

    public const int CurrentVersion = 2;

    public static bool IsRecognized(string? format, int version)
        => format == Id && version == CurrentVersion;
}
