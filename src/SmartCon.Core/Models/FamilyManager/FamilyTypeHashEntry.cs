namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One per-type content-hash row destined for the
/// <c>family_type_hashes</c> table (Issue #249, Phase 2). Computed at
/// Prepare time from the family snapshot and carried through the import
/// chain (PreparedFamilyItem → FamilyBatchImportItem → import request) so
/// the DB writer never re-opens the family file.
/// </summary>
/// <param name="TypeIdentityKey">
/// Identity key of the type row: <c>typeName.ToUpperInvariant()</c> for
/// loadable families (type names are unique within one family);
/// <see cref="SystemTypeIdentityKey.Build"/> ("TOKEN|NAME") for system
/// families, where one catalog category can hold same-named types of
/// different system families (FHV6). Matches the stale detector's
/// per-type map keys, so lookups join without re-keying.
/// </param>
/// <param name="TypeName">Type name as the user sees it (display).</param>
/// <param name="HashHex">SHA-256 hex of the type's canonical content
/// substring (see <c>IFamilyContentHasher.ComputePerTypeHashes*</c>).</param>
public sealed record FamilyTypeHashEntry(
    string TypeIdentityKey,
    string TypeName,
    string HashHex)
{
    /// <summary>
    /// Entry for a loadable-family type: the identity key is the
    /// upper-invariant type name (unique within one family).
    /// </summary>
    public static FamilyTypeHashEntry ForLoadableType(string typeName, string hashHex)
        => new(typeName.ToUpperInvariant(), typeName, hashHex);

    /// <summary>
    /// Entry for a system-family type: the identity key is the canonical
    /// <see cref="SystemTypeIdentityKey"/> of the type row.
    /// </summary>
    public static FamilyTypeHashEntry ForSystemType(SystemTypeContentHash typeHash)
        => new(typeHash.IdentityKey, typeHash.TypeName, typeHash.HashHex);
}
