using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Validates family catalog queries.
/// </summary>
public static class FamilyCatalogQueryValidator
{
    /// <summary>
    /// Validates and clamps a catalog query to safe bounds.
    /// </summary>
    public static FamilyCatalogQuery Validate(FamilyCatalogQuery query)
    {
#pragma warning disable CA1510
        if (query is null) throw new ArgumentNullException(nameof(query));
#pragma warning restore CA1510

        var offset = System.Math.Max(0, query.Offset);
        var limit = query.Limit < 1 ? 50 : System.Math.Min(query.Limit, 500);

        return query with { Offset = offset, Limit = limit };
    }
}
