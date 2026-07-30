using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Resolves routing-preference fitting dependencies (Issue #104). A fitting
/// is a regular loadable catalog family: first looked up in the project by
/// (family name, type name); when missing, it is loaded from the catalog
/// storage. When the family is absent from the catalog too, the dependency
/// stays unresolved — the caller skips the routing rule and reports it.
/// </summary>
/// <remarks>
/// Must be called on the Revit main thread (I-01) and OUTSIDE any open
/// transaction: <c>Document.LoadFamily</c> throws when the document is
/// modifiable. Synchronizers run this resolution before opening their
/// transaction.
/// </remarks>
public interface IFittingDependencyResolver
{
    /// <summary>
    /// Ensure the fitting symbol ("{familyName}:{typeName}") exists in the
    /// project; load it from the catalog when missing. Returns the symbol's
    /// id, or <c>null</c> when the family is neither in the project nor in
    /// the catalog.
    /// </summary>
    ElementId? EnsureFitting(
        Document activeDoc,
        string familyName,
        string typeName,
        int targetRevitVersion);
}
