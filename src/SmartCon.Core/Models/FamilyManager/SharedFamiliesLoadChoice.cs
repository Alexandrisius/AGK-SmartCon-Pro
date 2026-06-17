namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// User's choice for how to load a single shared nested family that conflicts
/// with an existing one already loaded in the project.
/// </summary>
public enum SharedFamiliesLoadChoice
{
    /// <summary>
    /// Keep the version already present in the project (FamilySource.Project).
    /// Safest option — no parameters touched, no type modifications.
    /// </summary>
    UseProject = 0,

    /// <summary>
    /// Load the new version from the .rfa but overwrite parameter values
    /// of existing types (FamilySource.Family + overwriteParameterValues = true).
    /// </summary>
    OverwriteParameters = 1,

    /// <summary>
    /// Full overwrite of the shared nested family and all its types
    /// (FamilySource.Family + overwriteParameterValues = true, same as
    /// OverwriteParameters in current Revit API — kept for future extensibility
    /// when Revit API distinguishes them via FamilySource.FamilySourceParameterValues).
    /// </summary>
    OverwriteAll = 2,

    /// <summary>
    /// Skip loading this shared nested family. OnSharedFamilyFound returns false,
    /// which causes Revit to abort loading the parent family as well.
    /// Used when the user closes the dialog via the X button.
    /// </summary>
    Skip = 3
}
