namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Kind of a single parameter-overwrite operation applied to a loaded
/// <c>FamilySymbol</c> in the project during the stale-update post-pass
/// (Issue #239). <see cref="ResolveElementByName"/> covers ElementId
/// parameters (e.g. Material): the catalog stores the resolved element NAME,
/// the Revit side re-resolves it to a live <c>ElementId</c> in the project.
/// </summary>
public enum TypeParameterOverwriteKind
{
    SetDouble,
    SetInteger,
    SetString,
    ResolveElementByName,
}
