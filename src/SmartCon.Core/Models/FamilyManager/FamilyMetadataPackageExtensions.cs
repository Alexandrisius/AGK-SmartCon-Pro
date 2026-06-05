namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Defensive normalization for <see cref="FamilyMetadataPackage"/>.
/// Returns an instance with non-null collections so downstream code
/// can iterate without NRE risk after deserializing a JSON payload
/// that has null or missing sections/categories/attributes/bindings
/// fields. All members are <c>init</c>-only on the record, so we
/// cannot mutate in place — we construct a fresh object only when
/// at least one collection is null.
/// </summary>
public static class FamilyMetadataPackageExtensions
{
    public static FamilyMetadataPackage WithNonNullCollections(this FamilyMetadataPackage source)
    {
        if (source.Sections is not null
            && source.Categories is not null
            && source.Attributes is not null
            && source.Bindings is not null)
        {
            return source;
        }

        return new FamilyMetadataPackage
        {
            Format = source.Format,
            Version = source.Version,
            ExportedAtUtc = source.ExportedAtUtc,
            Sections = source.Sections ?? new FamilyMetadataPackageSections(),
            Categories = source.Categories ?? [],
            Attributes = source.Attributes ?? [],
            Bindings = source.Bindings ?? []
        };
    }
}
