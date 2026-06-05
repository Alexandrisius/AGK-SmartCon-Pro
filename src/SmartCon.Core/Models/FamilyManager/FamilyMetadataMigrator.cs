using SmartCon.Core.Common;

namespace SmartCon.Core.Models.FamilyManager;

public static class FamilyMetadataMigrator
{
    public static FamilyMetadataPackage Migrate(FamilyMetadataPackage package)
    {
        Guard.ThrowIfNull(package);

        if (FamilyMetadataFormat.IsRecognized(package.Format, package.Version))
        {
            return package;
        }

        throw new NotSupportedException(
            $"Family metadata package format '{package.Format}' v{package.Version} is not supported. " +
            $"Expected '{FamilyMetadataFormat.Id}' v{FamilyMetadataFormat.CurrentVersion}.");
    }
}
